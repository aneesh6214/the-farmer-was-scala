case class Result(label: String, values: Map[String, Seq[Int]])
@main def drones(): Unit = {
  assert(max_drones() >= 2)
  val captured = 21
  var parentOnly = 1
  val child = spawn_drone(() => {
    parentOnly = 99
    assert(move(North))
    Result("worker", Map("value" -> Seq(captured * 2)))
  }).get
  val value = wait_for(child)
  assert(value == Result("worker", Map("value" -> Seq(42))))
  assert(parentOnly == 1)
  assert(has_finished(child))
  assert(num_drones() == 1)
  quick_print("DRONES_OK")
}
