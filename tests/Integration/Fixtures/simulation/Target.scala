@main def target(): Unit = {
  assert(simulation_global[Int]("answer", 0) == 42)
  assert(num_items(Items.Hay) == 10.0)
  assert(move(East))
  assert(get_pos_x() == 1)
}
