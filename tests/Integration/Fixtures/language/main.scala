import models.Crops.{Crop, describe}
@main def language(): Unit = {
  val crops: Seq[Crop] = (0 until 6).map(i => Crop(name = s"crop-$i", yieldCount = i))
  val selected: Map[String, Int] = crops.collect { case Crop(n, y) if y % 2 == 0 => n -> y }.toMap
  assert(selected.values.sum == 6)
  assert(describe(Crop("carrot", 3)) == "carrot:3")
  val missing: Option[Option[Int]] = Some(None)
  assert(missing.flatten.isEmpty)
  val result: Either[String, Int] = Right(42)
  assert(result.map(_ + 1) == Right(43))
  assert(get_world_size() == 8)
  quick_print("LANGUAGE_OK")
}
