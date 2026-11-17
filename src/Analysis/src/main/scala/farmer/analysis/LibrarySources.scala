package farmer.analysis

import java.nio.charset.StandardCharsets
import java.nio.file.{Files, Path}
import java.util.zip.ZipFile
import scala.jdk.CollectionConverters.*
import scala.meta.*

/**
  * Lazy source-only index. Library files never join the player's compiler outlines.
  */
final class LibrarySources(directory: Path):
  case class Definition(id: String, file: String, text: String, offset: Int, documentation: String, signature: String)
  private case class Entry(archive: Path, name: String)
  private val entries = if Files.isDirectory(directory) then
    val files = Files.list(directory)
    try files.iterator().asScala.filter(_.getFileName.toString.endsWith("-sources.jar")).toList.sorted.flatMap { archive =>
      val zip = new ZipFile(archive.toFile)
      try zip.entries().asScala.filter(e => !e.isDirectory && e.getName.endsWith(".scala") && e.getSize <= 524288)
        .map(e => Entry(archive, e.getName)).toList
      finally zip.close()
    }
    finally files.close()
  else Nil
  private val indexed = scala.collection.mutable.HashSet.empty[Entry]
  private val inheritedOwners = scala.collection.mutable.HashMap.empty[String, List[String]]
  private val definitions = scala.collection.mutable.HashMap.empty[String, Definition]

  private def read(entry: Entry): String =
    val zip = new ZipFile(entry.archive.toFile)
    try
      val stream = zip.getInputStream(zip.getEntry(entry.name))
      try new String(stream.readNBytes(524289), StandardCharsets.UTF_8)
      finally stream.close()
    finally zip.close()

  // A few public symbols share another type's file (Some/None in Option.scala,
  // Nil in List.scala). Build a small name-to-entry index only when needed.
  private lazy val names =
    val declaration = "(?m)^\\s*(?:(?:sealed|abstract|final|case|private|protected|open|opaque|implicit|lazy)\\s+)*(?:class|trait|object|enum|type)\\s+([\\p{L}_][\\p{L}\\p{N}_]*)".r
    entries.flatMap { entry => declaration.findAllMatchIn(read(entry)).map(m => m.group(1) -> entry).toList }.groupMap(_._1)(_._2)

  private def base(symbol: String): String = symbol.replaceAll("\\(\\+?\\d*\\)\\.$", "().")
  private def candidates(symbol: String): List[Entry] =
    val top = symbol.takeWhile(c => c != '#' && c != '.').stripSuffix("/")
    val paths = List(top + ".scala", top.substring(0, math.max(0, top.lastIndexOf('/'))) + "/package.scala")
    val direct = entries.filter(e => paths.exists(p => e.name == p || e.name.endsWith("/" + p)))
    // Scala 3 patches and generated package owners do not always share the symbol's path.
    val simple = top.substring(top.lastIndexOf('/') + 1).stripSuffix("$package") + ".scala"
    direct ++ entries.filter(e => !direct.contains(e) && e.name.endsWith("/" + simple))

  def find(symbol: String): Option[Definition] =
    if !symbol.startsWith("scala/") then return None
    if !definitions.contains(symbol) then candidates(symbol).foreach(index)
    if !definitions.contains(symbol) then
      val top = symbol.takeWhile(c => c != '#' && c != '.').stripSuffix("/")
      names.getOrElse(top.substring(top.lastIndexOf('/') + 1), Nil).foreach(index)
    definitions.get(symbol).orElse {
      // Case classes get a synthetic companion with no separate source range.
      if symbol.endsWith(".") && !symbol.contains('(') then definitions.get(symbol.dropRight(1) + "#") else None
    }.orElse {
      val normalized = base(symbol)
      val matches = definitions.iterator.collect { case (key, value) if base(key) == normalized => value }.toList
      if matches.size == 1 then matches.headOption else None
    }

  def documentationFor(symbol: String, seen: Set[String] = Set.empty): Option[String] =
    if seen(symbol) then None
    else
      val own = find(symbol).map(_.documentation).filter(_.nonEmpty)
      val split = symbol.lastIndexOf('#')
      val inherited = if split < 0 then None else
        inheritedOwners.getOrElse(symbol.take(split + 1), Nil).iterator
          .flatMap(owner => documentationFor(owner + symbol.drop(split + 1), seen + symbol)).take(1).toList.headOption
      own.map(text => inherited.fold(text)(parent => text.replace("@inheritdoc", parent))).orElse(inherited)

  private def index(entry: Entry): Unit =
    if !indexed.add(entry) then return
    val text = read(entry)
    if text.length > 524288 then return
    // The bundled compiler understands capture annotations newer than the
    // source-index parser. Mask only that type syntax, preserving every offset
    // and retaining the original text in the read-only source window.
    val captures = "\\^\\{[^}]*\\}".r.replaceAllIn(text, m => m.matched.map(c => if c == '\n' || c == '\r' then c else ' '))
    val compatible = "(?<=[\\p{L}\\p{N}_\\]\\)])\\^(?![=])".r.replaceAllIn(captures, " ")
    val input = Input.VirtualFile(entry.name, compatible)
    val parsed = dialects.Scala3(input).parse[Source].toOption.orElse(dialects.Scala213(input).parse[Source].toOption)
    parsed.foreach { source =>
      val overloads = scala.collection.mutable.HashMap.empty[String, Int]
      val macros = "(?m)^\\s*\\*?\\s*@define\\s+(\\w+)\\s+(.+)$".r.findAllMatchIn(text).map(m => m.group(1) -> m.group(2).trim).toMap
      val precedingDoc = DocComments.preceding(text)
      def doc(at: Int): String =
        val raw = precedingDoc(at)
        "\\$(\\w+)".r.replaceAllIn(raw, m => java.util.regex.Matcher.quoteReplacement(macros.getOrElse(m.group(1), m.group(1))))
      def add(symbol: String, name: Name, tree: Tree): Unit =
        val id = entry.archive.getFileName.toString + "!/" + entry.name
        val signature = tree match
          case value: Defn.Class => value.mods.filterNot(_.isInstanceOf[Mod.Annot]).map(_.syntax + " ").mkString + "class " + value.name.value +
            (if value.tparams.isEmpty then "" else value.tparams.map(_.syntax).mkString("[", ", ", "]")) + value.ctor.paramss.map(_.map(_.syntax).mkString("(", ", ", ")")).mkString
          case value: Defn.Trait => value.mods.filterNot(_.isInstanceOf[Mod.Annot]).map(_.syntax + " ").mkString + "trait " + value.name.value +
            (if value.tparams.isEmpty then "" else value.tparams.map(_.syntax).mkString("[", ", ", "]"))
          case value: Defn.Object => "object " + value.name.value
          case _ => ""
        definitions.getOrElseUpdate(symbol, Definition(id, entry.name, text, name.pos.start, doc(tree.pos.start), signature))
      def method(owner: String, name: Name, tree: Tree): Unit =
        val key = owner + name.value
        val count = overloads.getOrElse(key, 0); overloads(key) = count + 1
        add(key + (if count == 0 then "()." else s"(+$count)."), name, tree)
      def rememberParents(owner: String, enclosing: String, template: Template): Unit =
        def base(t: Type): String = t match
          case Type.Apply(tpe, _) => base(tpe)
          case _ => t.syntax
        inheritedOwners(owner) = template.inits.reverse.map { init =>
          val name = base(init.tpe).stripPrefix("_root_.")
          (if name.contains('.') then name.replace('.', '/') else enclosing + name) + "#"
        }
      def visit(tree: Tree, owner: String): Unit = tree match
        case pkg: Pkg => pkg.stats.foreach(visit(_, owner + pkg.ref.syntax.replace('.', '/') + "/"))
        case value: Pkg.Object =>
          val next = owner + value.name.value + "/package."
          rememberParents(next, owner, value.templ); add(next, value.name, value); value.templ.stats.foreach(visit(_, next))
        case value: Defn.Class =>
          val next = owner + value.name.value + "#"
          rememberParents(next, owner, value.templ); add(next, value.name, value); value.templ.stats.foreach(visit(_, next))
        case value: Defn.Trait =>
          val next = owner + value.name.value + "#"
          rememberParents(next, owner, value.templ); add(next, value.name, value); value.templ.stats.foreach(visit(_, next))
        case value: Defn.Object =>
          val next = owner + value.name.value + "."
          rememberParents(next, owner, value.templ); add(next, value.name, value); value.templ.stats.foreach(visit(_, next))
        case value: Defn.Enum =>
          val next = owner + value.name.value + "#"
          rememberParents(next, owner, value.templ); add(next, value.name, value); value.templ.stats.foreach(visit(_, next))
        case value: Defn.Def => method(owner, value.name, value)
        case value: Decl.Def => method(owner, value.name, value)
        case value: Defn.Type => add(owner + value.name.value + "#", value.name, value)
        case value: Decl.Type => add(owner + value.name.value + "#", value.name, value)
        case value: Defn.Val => value.pats.collect { case pat: Pat.Var => pat.name }.foreach(n => add(owner + n.value + ".", n, value))
        case value: Defn.Var => value.pats.collect { case pat: Pat.Var => pat.name }.foreach(n => add(owner + n.value + ".", n, value))
        case value: Decl.Val => value.pats.collect { case pat: Pat.Var => pat.name }.foreach(n => add(owner + n.value + ".", n, value))
        case value: Decl.Var => value.pats.collect { case pat: Pat.Var => pat.name }.foreach(n => add(owner + n.value + ".", n, value))
        case _ => tree.children.foreach(visit(_, owner))
      source.stats.foreach(visit(_, ""))
    }

