import Models.*
@main def editor(): Unit = quick_print("EDITOR_WARM_OK")
val carrot = new Carrot
val documented = carrot.yieldItems
val library: Option[Int] = Some(1)
val indexed = Seq(1, 2).zipWithIndex
val text = s"value: ${library.get}"
