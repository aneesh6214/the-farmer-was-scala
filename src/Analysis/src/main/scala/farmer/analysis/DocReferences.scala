package farmer.analysis

import scala.meta.*

/**
  * Analysis-only projection; original player text and diagnostic ranges never change.
  */
object DocReferences:
  case class Segment(original: Int, projected: Int, length: Int)
  case class Projection(text: String, segments: List[Segment]):
    def position(raw: Int): Option[Int] = segments.find(s => raw >= s.original && raw <= s.original + s.length).map(s => s.projected + raw - s.original)
    def original(start: Int, end: Int): Option[Int] = segments.find(s => start >= s.projected && end <= s.projected + s.length).map(s => s.original + start - s.projected)

  def project(raw: String): Projection =
    val comments = dialects.Scala3(Input.String(raw)).tokenize.toOption.toList.flatMap(_.collect {
      case comment: Token.Comment if comment.text.startsWith("/**") => comment
    })
    val output = new StringBuilder
    val segments = scala.collection.mutable.ListBuffer.empty[Segment]
    var previous = 0
    comments.foreach { comment =>
      val refs = scala.collection.mutable.ListBuffer.empty[(Int, Int)]
      val limit = comment.pos.end - (if comment.text.endsWith("*/") then 2 else 0)
      var at = comment.pos.start + 3
      while at < limit && refs.size < 64 do
        if raw.charAt(at) != '[' then at += 1
        else
          var depth = 1; var close = at + 1
          while close < limit && depth > 0 do
            if raw.charAt(close) == '[' then depth += 1
            else if raw.charAt(close) == ']' then depth -= 1
            if depth > 0 then close += 1
          var begin = at + 1; var end = close
          if begin < limit && raw.charAt(begin) == '[' then
            begin += 1
            if close < limit && raw.charAt(close - 1) == ']' then end -= 1
          if end >= begin then refs += begin -> end
          at = close + 1
      "@(?:throws|exception)[ \\t]+([^\\s*]*)".r.findAllMatchIn(comment.text).foreach { m =>
        if !m.group(1).startsWith("[") then refs += (comment.pos.start + m.start(1)) -> (comment.pos.start + m.end(1))
      }
      if refs.nonEmpty then
        output.append(raw.substring(previous, comment.pos.start))
        val line = raw.lastIndexOf('\n', math.max(0, comment.pos.start - 1)) + 1
        val indent = raw.substring(line, comment.pos.start).takeWhile(c => c == ' ' || c == '\t')
        refs.sortBy(_._1).foreach { case (begin, end) =>
          output.append("{\n" + indent + "  ")
          segments += Segment(begin, output.length, end - begin)
          output.append(raw.substring(begin, end)).append("\n" + indent + "};\n" + indent)
        }
        previous = comment.pos.end
    }
    output.append(raw.substring(previous))
    Projection(output.toString, segments.toList)
