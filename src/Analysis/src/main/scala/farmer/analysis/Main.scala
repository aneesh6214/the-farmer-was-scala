package farmer.analysis

import java.net.URI
import java.nio.charset.StandardCharsets
import java.nio.file.{Files, Path, Paths}
import java.util.Optional
import java.util.concurrent.{Executors, TimeUnit}
import scala.jdk.CollectionConverters.*
import scala.meta.pc.*
import scala.meta.internal.metals.EmptyCancelToken
import scala.meta.internal.pc.SemanticTokens
import dotty.tools.pc.ScalaPresentationCompiler
import com.google.gson.{Gson, JsonArray, JsonElement, JsonNull, JsonObject, JsonParser}
import org.eclipse.lsp4j.{Location, Position, Range}
import dotty.tools.dotc.semanticdb.{TextDocument, SymbolOccurrence, SymbolInformation}

/**
  * Private NDJSON service. Does not launch the farm program or bind game RPCs.
  */
object Main:
  private val gson = new Gson()
  private val executor = Executors.newSingleThreadScheduledExecutor()
  private var compiler: PresentationCompiler = null
  private var workspace: Path = null
  private var sdk: Path = null
  private var sourcePaths = List.empty[Path]
  private var currentSources = Map.empty[String, String]
  private var outline = List.empty[VirtualFileParams]
  private var documents = Map.empty[URI, (String, TextDocument)]
  private var definitions = Map.empty[String, List[Location]]
  private var documentation = Map.empty[String, String]
  private var libraries: LibrarySources = null
  private val search = new SymbolSearch:
    def documentation(requestedSymbol: String, parents: ParentSymbols): Optional[SymbolDocumentation] =
      Main.documentation.get(requestedSymbol).orElse(Option(libraries).flatMap(_.find(requestedSymbol)).map(_.documentation).filter(_.nonEmpty)).map { text => new SymbolDocumentation {
        def symbol(): String = requestedSymbol
        def displayName(): String = ""
        def docstring(): String = text
        def defaultValue(): String = ""
        def parameters(): java.util.List[SymbolDocumentation] = java.util.Collections.emptyList()
        def typeParameters(): java.util.List[SymbolDocumentation] = java.util.Collections.emptyList()
      }}.map(Optional.of).getOrElse(Optional.empty())
    def definition(symbol: String, uri: URI): java.util.List[Location] = definitions.getOrElse(symbol, Nil).asJava
    def definitionSourceToplevels(symbol: String, uri: URI): java.util.List[String] = java.util.Collections.emptyList()
    def search(query: String, target: String, visitor: SymbolSearchVisitor): SymbolSearch.Result = SymbolSearch.Result.COMPLETE
    def searchMethods(query: String, target: String, visitor: SymbolSearchVisitor): SymbolSearch.Result = SymbolSearch.Result.COMPLETE
  private val builtinNames = Set("harvest", "can_harvest", "swap", "plant", "move", "can_move", "till", "get_pos_x", "get_pos_y", "get_world_size", "get_entity_type", "get_ground_type", "get_time", "get_tick_count", "use_item", "get_water", "do_a_flip", "change_hat", "game_print", "quick_print", "num_items", "get_cost", "clear", "get_companion", "unlock", "num_unlocked", "measure", "random", "simulate", "set_execution_speed", "set_world_size", "spawn_drone", "num_drones", "max_drones", "wait_for", "has_finished", "pet_the_piggy", "tap", "Items", "Entities", "Grounds", "Hats", "Unlocks", "Direction", "North", "East", "South", "West")
  private def string(obj: JsonObject, key: String, default: String = ""): String =
    if obj.has(key) && !obj.get(key).isJsonNull then obj.get(key).getAsString else default
  private def int(obj: JsonObject, key: String, default: Int = 0): Int =
    if obj.has(key) then obj.get(key).getAsInt else default
  private def obj(fields: (String, Any)*): JsonObject =
    val value = new JsonObject()
    fields.foreach { case (key, item) =>
      item match
        case v: String => value.addProperty(key, v)
        case v: Int => value.addProperty(key, v)
        case v: Boolean => value.addProperty(key, v)
        case v: JsonElement => value.add(key, v)
        case null => value.add(key, JsonNull.INSTANCE)
        case v => value.addProperty(key, v.toString)
    }
    value
  private def array(values: Iterable[JsonElement]): JsonArray =
    val result = new JsonArray(); values.foreach(result.add); result
  private def await[A](future: java.util.concurrent.CompletableFuture[A]): A = future.get(15, TimeUnit.SECONDS)
  private def prefix(name: String): String =
    val parts = name.split('.').toList
    "package __farmer_player; " + parts.init.map(p => s"package `$p`; ").mkString +
      s"object `${parts.last}` {\nimport _root_.game.api.*; import _root_.__farmer_player.*\n"
  private def wrapped(name: String, text: String): String = prefix(name) + text + "\n}\n"

  private class Params(val uri: URI, val text: String, val offset: Int, includeOutline: Boolean, returnDiagnostics: Boolean = false) extends OffsetParams:
    def token(): CancelToken = EmptyCancelToken
    override def shouldReturnDiagnostics(): Boolean = returnDiagnostics
    override def outlineFiles(): Optional[OutlineFiles] =
      if !includeOutline then Optional.empty()
      else Optional.of(new OutlineFiles:
        def isFirstCompileSubstitute(): Boolean = true
        def files(): java.util.List[VirtualFileParams] = outline.filterNot(_.uri() == uri).asJava
      )

  private def configure(request: JsonObject): JsonObject =
    if compiler != null then compiler.shutdown()
    workspace = Paths.get(string(request, "workspace")).toAbsolutePath.normalize()
    sdk = Paths.get(string(request, "sdk")).toAbsolutePath.normalize()
    Files.createDirectories(workspace)
    val sourceDirectory = string(request, "librarySources")
    libraries = new LibrarySources(if sourceDirectory.nonEmpty then Paths.get(sourceDirectory) else sdk.getParent.resolve("tools/analysis/sources"))
    // Unsaved outlines supply all project/SDK definitions. Excluding the last
    // build's classes prevents deleted modules surviving in completion results.
    val classpath = request.getAsJsonArray("classpath").asScala.map(e => Paths.get(e.getAsString)).filter(_.toString.endsWith(".jar")).toList
    compiler = new ScalaPresentationCompiler()
      .withExecutorService(executor).withScheduledExecutorService(executor)
      .withSearch(search)
      .newInstance("farmer-scala", classpath.asJava, List("-scalajs").asJava, () => sourcePaths.asJava)
    documents = Map.empty; definitions = Map.empty; documentation = Map.empty
    obj("ok" -> true)

  private def capture(request: JsonObject): Unit =
    if compiler == null then throw new IllegalStateException("Configure the presentation compiler first.")
    currentSources = request.getAsJsonObject("sources").entrySet().asScala.map(e => e.getKey -> e.getValue.getAsString).toMap
    if currentSources.size > 128 || currentSources.values.map(_.length.toLong).sum > 1000000 then
      throw new IllegalArgumentException("Analysis source snapshot exceeds its limit.")
    val generated = currentSources.toList.map { case (name, text) =>
      if !name.matches("[\\p{L}_][\\p{L}\\p{N}_-]*(\\.[\\p{L}_][\\p{L}\\p{N}_-]*)*") then throw new IllegalArgumentException(s"Invalid source module name '$name'.")
      val path = workspace.resolve(name + ".scala")
      val source = wrapped(name, text)
      if !Files.exists(path) || Files.readString(path) != source then Files.writeString(path, source, StandardCharsets.UTF_8)
      new Params(path.toUri, source, 0, false)
    }
    currentSources.keys.foreach { name =>
      name.indices.filter(name.charAt(_) == '.').foreach { at =>
        if currentSources.keys.exists(_.equalsIgnoreCase(name.take(at))) then
          throw new IllegalArgumentException(s"Module '${name.take(at)}' is also used as a namespace by '$name'.")
      }
    }
    val sdkFiles = if Files.isDirectory(sdk) then
      val walk = Files.walk(sdk)
      try walk.iterator().asScala.filter(p => p.toString.endsWith(".scala")).map { p => new Params(p.toUri, Files.readString(p), 0, false) }.toList
      finally walk.close()
    else Nil
    outline = generated ++ sdkFiles
    sourcePaths = outline.map(p => Paths.get(p.uri()))
    // The public PC delegates cross-file definitions to SymbolSearch. Build that
    // table from the compiler's own SemanticDB definitions, never name matching.
    val active = outline.map(_.uri()).toSet
    val removed = documents.keys.filterNot(active.contains).toList
    removed.foreach(compiler.didClose)
    if removed.nonEmpty then
      removed.foreach { uri =>
        val path = Paths.get(uri).toAbsolutePath.normalize()
        if path.getParent == workspace && path.getFileName.toString.endsWith(".scala") then Files.deleteIfExists(path)
      }
      // The PC caches package-level symbols. A removed source must evict those
      // symbols as well as its open document, or stale classes can still infer.
      compiler.restart()
    documents = documents.filter((uri, _) => active.contains(uri))
    val changed = outline.filter(p => documents.get(p.uri()).forall(_._1 != p.text()))
    if changed.nonEmpty then
      val first = changed.head
      await(compiler.didChange(new Params(first.uri(), first.text(), 0, true)))
      changed.foreach { p =>
        val bytes = await(compiler.semanticdbTextDocument(p.uri(), p.text()))
        val document = TextDocument.parseFrom(bytes).asInstanceOf[TextDocument]
        documents += p.uri() -> (p.text() -> document)
      }
    if changed.nonEmpty || removed.nonEmpty then
      definitions = documents.toList.flatMap { case (uri, (_, document)) =>
        document.occurrences.filter(_.role == SymbolOccurrence.Role.DEFINITION).flatMap { occurrence =>
          occurrence.range.map { range => occurrence.symbol -> new Location(uri.toString,
            new Range(new Position(range.startLine, range.startCharacter), new Position(range.endLine, range.endCharacter))) }
        }
      }.groupMap(_._1)(_._2)
      documentation = documents.toList.flatMap { case (_, (text, document)) =>
        document.occurrences.filter(_.role == SymbolOccurrence.Role.DEFINITION).flatMap { occurrence =>
          occurrence.range.flatMap { range =>
            val at = rawOffset(text, range.startLine, range.startCharacter)
            val lineStart = text.lastIndexOf('\n', math.max(0, at - 1)) + 1
            val before = text.substring(0, lineStart).trim
            val open = before.lastIndexOf("/**")
            if !before.endsWith("*/") || open < 0 then None
            else Some(occurrence.symbol -> before.substring(open + 3, before.length - 2).linesIterator.map(_.trim.stripPrefix("*").trim).mkString("\n").trim)
          }
        }
      }.toMap

  private def params(request: JsonObject): Params =
    val file = string(request, "file")
    val raw = currentSources.getOrElse(file, throw new IllegalArgumentException("Unknown source module."))
    val offset = math.max(0, math.min(raw.length, int(request, "offset")))
    new Params(workspace.resolve(file + ".scala").toUri, wrapped(file, raw), prefix(file).length + math.max(0, offset - 1), true)

  private def isBuiltinSymbol(symbol: String): Boolean =
    symbol.startsWith("game/api.") || symbol.startsWith("game/api/") || definitions.getOrElse(symbol, Nil).exists { l =>
      try Paths.get(URI.create(l.getUri)).toAbsolutePath.normalize().startsWith(sdk)
      catch case _: Exception => false
    }
  private def isBuiltin(definition: DefinitionResult): Boolean =
    isBuiltinSymbol(definition.symbol()) ||
      definition.locations().asScala.exists { l =>
        try Paths.get(URI.create(l.getUri)).toAbsolutePath.normalize().startsWith(sdk)
        catch case _: Exception => false
      }
  private def rawOffset(text: String, line: Int, column: Int): Int =
    var at = 0; var current = 0
    while current < line && at < text.length do
      val newline = text.indexOf('\n', at)
      at = if newline < 0 then text.length else newline + 1
      current += 1
    math.min(text.length, at + column)
  private def resolve(request: JsonObject): JsonObject =
    val p = params(request)
    val definition = await(compiler.definition(p))
    val builtin = isBuiltin(definition)
    val rawTarget = definition.locations().asScala.iterator.flatMap { location =>
      try
        val path = Paths.get(URI.create(location.getUri)).toAbsolutePath.normalize()
        val name = path.getFileName.toString.stripSuffix(".scala")
        if path.getParent == workspace && currentSources.contains(name) then
          val source = currentSources(name)
          val offset = if location.getRange.getStart.getLine < 2 then 0
            else rawOffset(source, location.getRange.getStart.getLine - 2, location.getRange.getStart.getCharacter)
          val normalized = if offset > 0 && source.charAt(offset - 1) == '`' then offset - 1 else offset
          Some(obj("file" -> name, "offset" -> normalized))
        else None
      catch case _: Exception => None
    }.take(1).toList.headOption.orElse {
      if builtin then None
      else Option(libraries).flatMap(_.find(definition.symbol())).map { value =>
        obj("file" -> Paths.get(value.file).getFileName.toString.stripSuffix(".scala"), "offset" -> value.offset,
          "isLibrary" -> true, "source" -> value.text, "libraryId" -> value.id)
      }
    }
    val symbol = definition.symbol()
    // A package can occur in several source files. Prefer a deterministic
    // child instead of the arbitrary package location returned by the PC.
    val namespaceTarget = if symbol.startsWith("__farmer_player/") && symbol.endsWith("/") then
      val path = symbol.stripPrefix("__farmer_player/").stripSuffix("/").replace('/', '.')
      currentSources.keys.toList.sorted.find(n => n == path || n.startsWith(path + ".")).map(n => obj("file" -> n, "offset" -> 0))
    else None
    val target = namespaceTarget.orElse(rawTarget)
    val hover = await(compiler.hover(p))
    val signatureAndDoc = if hover.isPresent then
      val lsp = hover.get.toLsp()
      val contents = lsp.getContents
      val markdown = if contents == null then "" else if contents.isRight then contents.getRight.getValue else
        contents.getLeft.asScala.map(v => if v.isLeft then v.getLeft else v.getRight.getValue).mkString("\n\n")
      markdown.replace("```scala\n", "").replace("```java\n", "").replace("```", "").trim
    else ""
    // Keep structured Scaladoc tags intact for the game's common renderer.
    // The PC's markdown conversion flattens parameter tags into loose prose.
    val libraryDefinition = Option(libraries).flatMap(_.find(definition.symbol()))
    val enumSignature = SdkEnums.describe(sdk, definition.symbol())
    // A bare enum name resolves to its companion (.) while Scaladoc belongs
    // to the enum type (#). Retain the explicit enum signature even without docs.
    val enumDoc = if enumSignature.nonEmpty then documentation.get(symbol.stripSuffix(".") + "#") else None
    val doc = documentation.get(definition.symbol()).orElse(enumDoc).orElse(libraryDefinition.map(_.documentation)).filter(_.nonEmpty)
    val signature = enumSignature.orElse(libraryDefinition.map(_.signature).filter(_.nonEmpty)).getOrElse(signatureAndDoc.split("\n\n", 2).headOption.getOrElse(""))
    val text = doc.map(value => signature + "\n\n" + value).getOrElse(enumSignature.getOrElse(signatureAndDoc))
    val gameFunction = if builtin && definition.symbol().startsWith("game/api.") then
      val name = definition.symbol().stripPrefix("game/api.").takeWhile(c => c != '(' && c != '.')
      if name.matches("[A-Za-z_][A-Za-z_0-9]*") then name else null
    else null
    obj("ok" -> true, "isGameBuiltin" -> builtin, "gameFunctionName" -> gameFunction, "definition" -> target.orNull, "hover" -> obj("text" -> text))

  private def diagnostics(request: JsonObject): JsonObject =
    val values = currentSources.toList.sortBy(_._1).flatMap { case (name, raw) =>
      val full = wrapped(name, raw)
      val p = new Params(workspace.resolve(name + ".scala").toUri, full, 0, true, true)
      await(compiler.didChange(p)).asScala.flatMap { diagnostic =>
        val range = diagnostic.getRange
        if range == null || range.getStart.getLine < 2 then None
        else
          val start = rawOffset(raw, range.getStart.getLine - 2, range.getStart.getCharacter)
          val end = rawOffset(raw, range.getEnd.getLine - 2, range.getEnd.getCharacter)
          val severity = if diagnostic.getSeverity == org.eclipse.lsp4j.DiagnosticSeverity.Warning then "warning" else "error"
          val message = diagnostic.getMessage
          val plain = if message == null then "Scala error" else if message.isLeft then message.getLeft
            else Option(message.getRight).map(_.getValue).getOrElse("Scala error")
          Some(obj("file" -> name, "start" -> start, "length" -> math.max(0, end - start),
            "message" -> plain, "severity" -> severity))
      }.toList
    }
    obj("ok" -> true, "diagnostics" -> array(values))

  private def format(request: JsonObject): JsonObject =
    val name = string(request, "file")
    val sources = request.getAsJsonObject("sources")
    if sources == null || sources.size > 128 || sources.entrySet().asScala.map(_.getValue.getAsString.length.toLong).sum > 1000000 then
      throw new IllegalArgumentException("Formatting source snapshot exceeds its limit.")
    val raw = Option(sources.get(name)).map(_.getAsString).getOrElse(throw new IllegalArgumentException("Unknown source module."))
    ScriptFormatter.format(raw) match
      case Right(source) => obj("ok" -> true, "source" -> source)
      case Left(error) => obj("ok" -> true, "error" -> error)

  private def complete(request: JsonObject): JsonObject =
    val p = params(request)
    // Completion offsets point after the typed prefix; unlike hover they are not decremented.
    val file = string(request, "file")
    val projection = DocReferences.project(currentSources(file))
    val rawCursor = int(request, "offset")
    val completionParams = projection.position(rawCursor) match
      case Some(offset) => new Params(p.uri, wrapped(file, projection.text), prefix(file).length + offset, true)
      case None => new Params(p.uri, p.text, math.min(p.text.length, p.offset + 1), true)
    val result = await(compiler.complete(completionParams))
    val names = result.getItems.asScala.map { item =>
      Option(item.getFilterText).filter(_.nonEmpty).getOrElse(item.getLabel.takeWhile(c => c != '(' && c != '[' && !c.isWhitespace))
    }.filter(_.nonEmpty).distinct.sorted
    val values = new JsonArray(); names.foreach(values.add)
    obj("ok" -> true, "completions" -> values)

  private def analyze(request: JsonObject): JsonObject =
    val p = params(request)
    val raw = currentSources(string(request, "file"))
    val start = prefix(string(request, "file")).length
    val occurrences = documents.get(p.uri).toList.flatMap { case (_, document) =>
      document.occurrences.flatMap(o => o.range.map(r => rawOffset(p.text, r.startLine, r.startCharacter) -> o.symbol))
    }
    val symbols = occurrences.toMap
    val symbolKinds = documents.valuesIterator.flatMap(_._2.symbols.map { s =>
      val value = (s.properties & (SymbolInformation.Property.VAL.value | SymbolInformation.Property.VAR.value)) != 0
      s.symbol -> (if value then "FIELD" else s.kind.toString)
    }).toMap
    val nodes = await(compiler.semanticTokens(p)).asScala.iterator.flatMap { node =>
      val at = node.start() - start; val end = node.end() - start
      if at < 0 || end > raw.length || end <= at then None
      else
        val tokenType = SemanticTokens.TokenTypes.lift(node.tokenType()).getOrElse("")
        var kind = if Set("class", "enum", "interface", "type", "typeParameter").contains(tokenType) then "class"
          else if Set("method", "function", "macro").contains(tokenType) then "function" else "value"
        symbols.get(node.start()).flatMap(symbolKinds.get).foreach { declared =>
          if Set("METHOD", "MACRO").contains(declared) then kind = "function"
          else if Set("CLASS", "TRAIT", "TYPE", "TYPE_PARAMETER", "OBJECT", "INTERFACE", "CONSTRUCTOR").contains(declared) then kind = "class"
          else if Set("LOCAL", "FIELD", "PARAMETER").contains(declared) then kind = "value"
        }
        if symbols.get(node.start()).exists(isBuiltinSymbol) then kind = "builtin"
        // PC can attach the result type's synthetic token to an unapplied
        // method's name. Do not paint `farmer.run` as its return type `Int`.
        // The exact-source local index retains obvious method reference colors.
        val semanticSymbol = symbols.getOrElse(node.start(), "")
        val descriptor = semanticSymbol.stripSuffix("#").stripSuffix(".")
        val simple = descriptor.substring(descriptor.lastIndexWhere(c => c == '/' || c == '#' || c == '.') + 1).stripPrefix("`").stripSuffix("`")
        val unrelatedType = kind == "class" && semanticSymbol.nonEmpty && raw.substring(at, end).stripPrefix("`").stripSuffix("`") != simple
        if unrelatedType then None
        else Some(obj("start" -> at, "length" -> (end - at), "kind" -> kind))
    }.toList
    val projection = DocReferences.project(raw)
    val documentationNodes = if projection.segments.isEmpty then Nil else
      val projected = new Params(p.uri, wrapped(string(request, "file"), projection.text), 0, true)
      val projectedDocument = TextDocument.parseFrom(await(compiler.semanticdbTextDocument(projected.uri, projected.text))).asInstanceOf[TextDocument]
      val projectedSymbols = projectedDocument.occurrences.flatMap(o => o.range.map(r => rawOffset(projected.text, r.startLine, r.startCharacter) -> o.symbol)).toMap
      await(compiler.semanticTokens(projected)).asScala.toList.flatMap { node =>
        val begin = node.start() - start; val end = node.end() - start
        projection.original(begin, end).map { original =>
          val tokenType = SemanticTokens.TokenTypes.lift(node.tokenType()).getOrElse("")
          var kind = if Set("class", "enum", "interface", "type", "typeParameter").contains(tokenType) then "class"
            else if Set("method", "function", "macro").contains(tokenType) then "function" else "value"
          projectedSymbols.get(node.start()).flatMap(symbolKinds.get).foreach { declared =>
            if Set("METHOD", "MACRO").contains(declared) then kind = "function"
            else if Set("CLASS", "TRAIT", "TYPE", "TYPE_PARAMETER", "OBJECT", "INTERFACE", "CONSTRUCTOR").contains(declared) then kind = "class"
            else if Set("LOCAL", "FIELD", "PARAMETER").contains(declared) then kind = "value"
          }
          if projectedSymbols.get(node.start()).exists(isBuiltinSymbol) then kind = "builtin"
          obj("start" -> original, "length" -> (end - begin), "kind" -> kind)
        }
      }
    val unique = (nodes ++ documentationNodes).groupBy(n => n.get("start").getAsInt -> n.get("length").getAsInt).values.map { alternatives =>
      alternatives.maxBy(n => n.get("kind").getAsString match
        case "builtin" => 4
        case "class" => 3
        case "function" => 2
        case _ => 1)
    }.toList.sortBy(_.get("start").getAsInt)
    obj("ok" -> true, "spans" -> array(unique))

  def main(args: Array[String]): Unit =
    val input = scala.io.Source.fromInputStream(System.in, "UTF-8").getLines()
    val output = new java.io.PrintWriter(new java.io.OutputStreamWriter(System.out, StandardCharsets.UTF_8), true)
    try
      input.foreach { line =>
        var id: JsonElement = JsonNull.INSTANCE
        try
          if line.length > 4000000 then throw new IllegalArgumentException("Analysis frame exceeds limit.")
          val request = JsonParser.parseString(line).getAsJsonObject
          id = request.get("id")
          val result = if string(request, "op") == "configure" then configure(request)
          else if string(request, "op") == "format" then format(request)
          else
            capture(request)
            string(request, "op") match
              case "resolve" => resolve(request)
              case "complete" => complete(request)
              case "analyze" => analyze(request)
              case "diagnostics" => diagnostics(request)
              case _ => throw new IllegalArgumentException("Unknown analysis operation.")
          result.add("id", id)
          output.println(gson.toJson(result))
        catch case error: Exception =>
          val result = obj("ok" -> false, "error" -> Option(error.getMessage).getOrElse(error.getClass.getSimpleName))
          result.add("id", id); output.println(gson.toJson(result))
      }
    finally
      if compiler != null then compiler.shutdown()
      executor.shutdownNow()
