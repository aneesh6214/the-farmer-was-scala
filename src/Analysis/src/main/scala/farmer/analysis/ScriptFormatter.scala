package farmer.analysis

import scala.meta.*

/**
  * Explicit, pure source formatting. No build/link tasks and no source writes.
  */
object ScriptFormatter:
  private val configs = scala.collection.mutable.Map.empty[Int, org.scalafmt.config.ScalafmtConfig]
  private def config(indentSize: Int) = configs.getOrElseUpdate(indentSize, org.scalafmt.Scalafmt.parseHoconConfig(s"""
    version = 3.11.5
    runner.dialect = scala3
    indent.main = $indentSize
    indent.significant = $indentSize
    indent.callSite = $indentSize
    indent.defnSite = $indentSize
    maxColumn = 100
    docstrings.style = Asterisk
    docstrings.oneline = unfold
    docstrings.blankFirstLine = unfold
    rewrite.rules = [Imports]
    rewrite.imports.sort = ascii
    rewrite.imports.selectors = fold
    rewrite.imports.removeRedundantSelectors = true
    rewrite.imports.groups = [["java[.].*", "javax[.].*", "scala[.].*" ]]
    rewrite.imports.contiguousGroups = only
    rewrite.imports.sortCatchallGroup = tail
    rewrite.scala3.convertToNewSyntax = false
    rewrite.scala3.removeOptionalBraces = no
    """).get)

  def format(raw: String, indentSize: Int): Either[String, String] =
    try
      require(indentSize >= 1 && indentSize <= 8, "Invalid editor indentation size.")
      val wrapper = "object __FarmerFormat {\n" + raw + "\n}\n"
      val formatted = org.scalafmt.Scalafmt.format(wrapper, config(indentSize)).get
      val open = formatted.indexOf('{'); val close = formatted.lastIndexOf('}')
      if open < 0 || close <= open then throw new IllegalStateException("Formatter did not preserve the script wrapper.")
      val begin = if formatted.charAt(open + 1) == '\n' then open + 2 else open + 1
      val body = formatted.substring(begin, close).stripSuffix("\n")
      val protectedRanges = dialects.Scala3(Input.String(formatted)).tokenize.get.collect {
        case token: Token.Constant.String => token.pos.start -> token.pos.end
        case token: Token.Interpolation.Part => token.pos.start -> token.pos.end
      }.toList
      var at = begin
      val source = body.linesIterator.map { line =>
        val insideLiteral = protectedRanges.exists((start, end) => start < at && at < end)
        at += line.length + 1
        if !insideLiteral && line.startsWith(" " * indentSize) then line.drop(indentSize) else line
      }.mkString("\n")
      // Formatting and wrapper removal may change indentation but must preserve
      // literal contents, including interpolated/triple-quoted strings.
      def literalValues(code: String): List[String] =
        dialects.Scala3(Input.String(code)).parse[Source].get.collect { case value: Lit.String => value.value }
      if literalValues(wrapper) != literalValues("object __FarmerFormat {\n" + source + "\n}\n") then
        throw new IllegalStateException("Formatting would alter string literal contents.")
      Right(if source.isEmpty then "" else source + "\n")
    catch case error: Exception => Left("Cannot format incomplete or invalid Scala: " + Option(error.getMessage).getOrElse(error.getClass.getSimpleName))
