@main def simulation(): Unit = {
  val before = num_items(Items.Hay)
  val elapsed = simulate("Target", Map(Unlocks.Expand -> 5, Unlocks.Loops -> 1, Unlocks.Debug -> 1), Map(Items.Hay -> 10.0), Map("answer" -> 42), seed = 123, speedup = 32.0)
  assert(elapsed >= 0)
  assert(num_items(Items.Hay) == before)
  assert(get_pos_x() == 0 && get_pos_y() == 0)
  quick_print("SIMULATION_OK")
}
