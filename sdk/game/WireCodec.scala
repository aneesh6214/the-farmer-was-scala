package game

import scala.scalajs.js
import scala.deriving.Mirror
import scala.compiletime.{erasedValue, summonInline, constValue}

/**
  * Explicit wire codecs keep worker values typed without exposing CLR objects.
  * Case classes/enums derive automatically; ordinary classes can provide a codec.
  */
trait WireCodec[A]:
  def encode(value: A): js.Any
  def decode(value: js.Any): A

object WireCodec:
  def apply[A](using codec: WireCodec[A]): WireCodec[A] = codec
  private[game] def obj(value: js.Any): js.Dynamic = value.asInstanceOf[js.Dynamic]
  private[game] def tagged(tag: String, values: js.Array[js.Any]): js.Any =
    js.Dynamic.literal("$" -> tag, "items" -> values)
  private[game] def items(value: js.Any): js.Array[js.Any] = obj(value).items.asInstanceOf[js.Array[js.Any]]
  private def codec[A](write: A => js.Any, read: js.Any => A): WireCodec[A] = new WireCodec[A]:
    def encode(value: A): js.Any = write(value)
    def decode(value: js.Any): A = read(value)
  given WireCodec[Unit] = codec(_ => null, _ => ())
  given WireCodec[Boolean] = codec(_.asInstanceOf[js.Any], _.asInstanceOf[Boolean])
  given WireCodec[String] = codec(_.asInstanceOf[js.Any], _.asInstanceOf[String])
  given WireCodec[Int] = codec(_.asInstanceOf[js.Any], _.asInstanceOf[Double].toInt)
  given WireCodec[Double] = codec(value => { require(!value.isNaN && !value.isInfinity, "Non-finite game number"); value.asInstanceOf[js.Any] }, _.asInstanceOf[Double])
  given WireCodec[Float] = codec(value => summon[WireCodec[Double]].encode(value.toDouble), _.asInstanceOf[Double].toFloat)
  given WireCodec[Long] = codec(value => js.Dynamic.literal("$" -> "long", "value" -> value.toString), value => obj(value).value.asInstanceOf[String].toLong)
  given WireCodec[Short] = codec(_.toInt.asInstanceOf[js.Any], _.asInstanceOf[Double].toShort)
  given WireCodec[Byte] = codec(_.toInt.asInstanceOf[js.Any], _.asInstanceOf[Double].toByte)
  given WireCodec[Char] = codec(_.toString.asInstanceOf[js.Any], _.asInstanceOf[String].head)
  given [A: WireCodec]: WireCodec[Option[A]] = codec(
    value => value.fold[js.Any](null)(v => js.Dynamic.literal("$" -> "option", "value" -> summon[WireCodec[A]].encode(v))),
    value =>
      if value == null then None
      else if js.typeOf(value) == "object" && obj(value).selectDynamic("$").asInstanceOf[String] == "option" then Some(summon[WireCodec[A]].decode(obj(value).value))
      else Some(summon[WireCodec[A]].decode(value)))
  given [A: WireCodec]: WireCodec[List[A]] = codec(
    value => js.Array(value.map(summon[WireCodec[A]].encode)*),
    value => value.asInstanceOf[js.Array[js.Any]].iterator.map(summon[WireCodec[A]].decode).toList)
  given [A: WireCodec]: WireCodec[Vector[A]] = codec(
    value => js.Array(value.map(summon[WireCodec[A]].encode)*),
    value => value.asInstanceOf[js.Array[js.Any]].iterator.map(summon[WireCodec[A]].decode).toVector)
  given [A: WireCodec]: WireCodec[Seq[A]] = codec(
    value => js.Array(value.map(summon[WireCodec[A]].encode)*),
    value => value.asInstanceOf[js.Array[js.Any]].iterator.map(summon[WireCodec[A]].decode).toSeq)
  given [A: WireCodec]: WireCodec[Set[A]] = codec(
    value => tagged("set", js.Array(value.iterator.map(summon[WireCodec[A]].encode).toSeq*)),
    value => items(value).iterator.map(summon[WireCodec[A]].decode).toSet)
  given [K: WireCodec, V: WireCodec]: WireCodec[Map[K, V]] = codec(
    value => tagged("dict", js.Array(value.iterator.map((k, v) => js.Array(summon[WireCodec[K]].encode(k), summon[WireCodec[V]].encode(v)).asInstanceOf[js.Any]).toSeq*)),
    value => items(value).iterator.map { pair => val a = pair.asInstanceOf[js.Array[js.Any]]; (summon[WireCodec[K]].decode(a(0)), summon[WireCodec[V]].decode(a(1))) }.toMap)

  private inline def codecs[T <: Tuple]: List[WireCodec[Any]] = inline erasedValue[T] match
    case _: EmptyTuple => Nil
    case _: (head *: tail) => summonInline[WireCodec[head]].asInstanceOf[WireCodec[Any]] :: codecs[tail]

  /**
    * Derivation includes products (case classes and tuples) and sealed sums.
    */
  inline given derived[A](using mirror: Mirror.Of[A]): WireCodec[A] =
    val members = codecs[mirror.MirroredElemTypes]
    inline mirror match
      case product: Mirror.ProductOf[A] =>
        val label = constValue[mirror.MirroredLabel].asInstanceOf[String]
        new WireCodec[A]:
          def encode(value: A): js.Any =
            val elements = value.asInstanceOf[Product].productIterator
            val values = js.Array(members.map(c => c.encode(elements.next()))*)
            // Native tuples retain their native wire tag; records are opaque
            // Scala values when crossing the game bridge.
            if label.startsWith("Tuple") then tagged("tuple", values)
            else js.Dynamic.literal("$" -> "record", "type" -> label, "items" -> values)
          def decode(value: js.Any): A =
            if !label.startsWith("Tuple") then require(obj(value).selectDynamic("type").asInstanceOf[String] == label, "Wire record type mismatch")
            val values = items(value)
            require(values.length == members.length, "Wire product arity mismatch")
            val decoded = members.zipWithIndex.map((c, i) => c.decode(values(i))).toArray
            product.fromProduct(Tuple.fromArray(decoded))
      case sum: Mirror.SumOf[A] =>
        new WireCodec[A]:
          def encode(value: A): js.Any =
            val index = sum.ordinal(value)
            js.Dynamic.literal("$" -> "sum", "index" -> index, "value" -> members(index).encode(value))
          def decode(value: js.Any): A =
            val index = obj(value).index.asInstanceOf[Int]
            require(index >= 0 && index < members.length, "Wire sum case mismatch")
            members(index).decode(obj(value).value).asInstanceOf[A]
