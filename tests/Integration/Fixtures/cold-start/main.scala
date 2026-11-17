@main def integrationSmoke(): Unit = {
  assert(get_world_size() == 8)
  val entity: Entities = Entities.Grass
  assert(entity == Entities.Grass)
  val invalidCase: Entities = Entities.Does_Not_Exist
  val before = get_pos_x()
  assert(move(East))
  assert(get_pos_x() == (before + 1) % 8)
  quick_print("INTEGRATION_SMOKE_OK")
}
