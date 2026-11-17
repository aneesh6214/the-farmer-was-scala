@main def mining(): Unit = {
  val crops: Seq[Entities] = Seq(Entities.Bamboo, Entities.Rice)
  val groundToItem: Map[Grounds, Items] = Map(Grounds.Coal -> Items.Coal, Grounds.Quartz -> Items.Quartz)
  assert(crops.size == 2 && groundToItem.size == 2)
  jump(Unlocks.Coal)
  val start = get_pos_z()
  assert(start < 0)
  val hardness: Option[Double] = get_hardness()
  val stability: Option[Double] = get_stability(Some(East))
  assert(hardness.nonEmpty && stability.nonEmpty)
  assert(get_stability().nonEmpty && get_hardness(Some(East)).nonEmpty)
  var removed = 0
  for (_ <- 0 until 12) if (dig()) removed += 1
  assert(removed > 0 && get_pos_z() < start)
  val iron: Option[Directions] = prospect_iron()
  val quartz: Option[Double] = prospect_quartz()
  val measurement: Option[Double | (Int, Int) | String] = measure()
  val changed: Boolean = hover(2)
  assert(get_hover() == 2)
  val placed: Boolean = place(Grounds.Block_Red)
  val possible: Boolean = can_push(East)
  if (possible) push(East)
  quick_print(s"MINING_OK removed=$removed z=${get_pos_z()} iron=$iron quartz=$quartz placed=$placed")
}
