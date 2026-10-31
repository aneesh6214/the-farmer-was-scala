package farmer.analysis

import java.nio.file.{Files, Path}

object SdkEnums:
  private var cachedPath: Path = null
  private var cachedText = ""
  private var signatures = Map.empty[String, String]
  def describe(sdk: Path, symbol: String): Option[String] =
    if !symbol.startsWith("game/") then return None
    val path = sdk.resolve("game/GameEnums.scala")
    if !Files.exists(path) then return None
    val text = Files.readString(path)
    if path != cachedPath || text != cachedText then
      cachedPath = path; cachedText = text
      signatures = "(?ms)^enum (\\w+)[^\\r\\n]*:\\r?\\n(.*?)(?=^object |\\z)".r.findAllMatchIn(text).map { m =>
        val cases = "(?m)^  case ([^\\r\\n]+)".r.findAllMatchIn(m.group(2)).map(_.group(1)).mkString(", ")
        m.group(1) -> ("enum " + m.group(1) + ":\n  case " + cases)
      }.toMap
    val name = symbol.stripSuffix(".").stripSuffix("#").split("[/#.]+").lastOption.getOrElse("").takeWhile(_ != '(')
    signatures.get(name)
