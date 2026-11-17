package farmer.analysis

import scala.meta.*

/** Associate comments with complete declarations, not individual keyword tokens. */
object DocComments:
  /** Only a directly preceding doc token belongs to the declaration. */
  def preceding(text: String): Int => String =
    val comments = dialects.Scala3(Input.String(text)).tokenize.toOption.toList.flatMap(_.collect {
      case comment: Token.Comment => comment
    }).reverse
    at => comments.find(_.pos.end <= at).filter { comment =>
      comment.syntax.startsWith("/**") && text.substring(comment.pos.end, at).forall(_.isWhitespace)
    }.map { comment =>
      comment.syntax.stripPrefix("/**").stripSuffix("*/").linesIterator.map(_.trim.stripPrefix("*").trim).mkString("\n").trim
    }.getOrElse("")

  def in(text: String): Map[Int, String] =
    val commentAt = preceding(text)
    dialects.Scala3(Input.String(text)).parse[Source].toOption.toList.flatMap { source =>
      source.collect {
        case t: Defn.Def => (t.name.pos.start, t.pos.start)
        case t: Decl.Def => (t.name.pos.start, t.pos.start)
        case t: Defn.Class => (t.name.pos.start, t.pos.start)
        case t: Defn.Trait => (t.name.pos.start, t.pos.start)
        case t: Defn.Object => (t.name.pos.start, t.pos.start)
        case t: Defn.Enum => (t.name.pos.start, t.pos.start)
        case t: Defn.Type => (t.name.pos.start, t.pos.start)
        case t: Decl.Type => (t.name.pos.start, t.pos.start)
        case t: Pat.Var if t.parent.exists(p => p.isInstanceOf[Defn.Val] || p.isInstanceOf[Defn.Var] || p.isInstanceOf[Decl.Val] || p.isInstanceOf[Decl.Var]) =>
          (t.name.pos.start, t.parent.get.pos.start)
      }.flatMap { (name, start) =>
        Option(commentAt(start)).filter(_.nonEmpty).map(name -> _)
      }
    }.toMap
