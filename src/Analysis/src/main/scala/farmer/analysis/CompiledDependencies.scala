package farmer.analysis

import java.nio.file.{Files, Paths}
import scala.jdk.CollectionConverters.*
import dotty.tools.dotc.semanticdb.{TextDocuments, SymbolOccurrence}
import com.google.gson.Gson

/** Read resolved references from the successful build, never from editable imports. */
object CompiledDependencies:
  def main(args: Array[String]): Unit =
    val root = Paths.get(args(0)).resolve("target")
    val entry = args(1)
    val names = args.drop(2).toSet
    val owners = names.toList.map { name =>
      val parts = name.split('.').toList
      ("__farmer_player/" + parts.init.mkString("/") + (if parts.init.isEmpty then "" else "/") + parts.last + ".") -> name
    }.sortBy(-_._1.length)
    val graph = scala.collection.mutable.Map.empty[String, Set[String]]
    val paths = Files.walk(root)
    try paths.iterator.asScala.filter(p => p.toString.endsWith(".semanticdb")).foreach { path =>
      TextDocuments.parseFrom(Files.readAllBytes(path)).documents.foreach { document =>
        val name = document.uri.replace('\\', '/').split('/').last.stripSuffix(".scala")
        if names(name) then
          val symbols = document.occurrences.filter(_.role == SymbolOccurrence.Role.REFERENCE).map(_.symbol)
          val synthetic = document.synthetics.mkString("\n").replace("`", "")
          val refs = symbols.flatMap(symbol => owners.find((owner, _) => symbol.replace("`", "").startsWith(owner)).map(_._2)).toSet ++
            owners.collect { case (owner, name) if synthetic.contains(owner) => name }
          // A simulation can select a source window by a runtime string. Keep
          // those potentially selected modules pinned conservatively.
          val dynamic = symbols.exists(s => s.startsWith("game/api.simulate"))
          graph(name) = if dynamic then names else refs
      }
    } finally paths.close()
    require(graph.contains(entry), "The entry has no compiled dependency metadata.")
    val seen = scala.collection.mutable.Set.empty[String]
    def visit(name: String): Unit = if seen.add(name) then graph.getOrElse(name, Set.empty).foreach(visit)
    visit(entry)
    println(new Gson().toJson(seen.toList.sorted.asJava))
