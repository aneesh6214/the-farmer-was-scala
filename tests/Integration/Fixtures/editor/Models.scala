abstract class Method {
  /** Inventory items produced by this crop. */
  val yieldItems: Set[Items]
  /** Handles the requested tile. */
  def handleTile(tile: Int): Unit
}
class Carrot extends Method {
  override val yieldItems: Set[Items] = Set(Items.Carrot)
  /** Plant [[Entities.Carrot]].
   * @param tile the coordinate.
   */
  override def handleTile(tile: Int): Unit = ()
}
