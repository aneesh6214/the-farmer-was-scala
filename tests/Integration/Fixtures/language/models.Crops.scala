case class Crop(name: String, yieldCount: Int)
def describe(crop: Crop): String = crop match {
  case Crop(name, count) => s"$name:$count"
}
@main def notImported(): Unit = throw new Exception("An imported entry point ran")
