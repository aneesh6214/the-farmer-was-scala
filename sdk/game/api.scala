package game

import scala.scalajs.js
import scala.scalajs.js.annotation.JSGlobal

@js.native
@JSGlobal("__farmerAction")
private object Action extends js.Function2[String, String, String]:
  def apply(operation: String, arguments: String): String = js.native

@js.native
@JSGlobal("__farmerSpawn")
private object Spawn extends js.Function1[js.Function0[String], String]:
  def apply(callback: js.Function0[String]): String = js.native

@js.native
@JSGlobal("__farmerGlobals")
private object Globals extends js.Object

/**
  * Typed equivalents of the game's native built-ins, imported into each code window.
  * Farm actions retain the game's action costs and physical behavior. Scala-side
  * calculations are executed by the Scala.js runtime, outside the game's interpreter.
  * Missing results use `Option`: `Some(value)` means a result exists; `None` means it does not.
  */
object api:
  export game.{WireCodec, Direction, Entities, Grounds, Items, Unlocks, Hats, Drone, SimulationGlobals}
  type Entity = Entities
  type Ground = Grounds
  type Item = Items
  type Unlock = Unlocks
  type Hat = Hats
  val North: Direction = Direction.North
  val East: Direction = Direction.East
  val South: Direction = Direction.South
  val West: Direction = Direction.West

  private def wire[A: WireCodec](value: A): js.Any = summon[WireCodec[A]].encode(value)

  private def call[A: WireCodec](operation: String, args: js.Any*): A =
    val result = js.JSON.parse(Action(operation, js.JSON.stringify(js.Array(args*))))
    summon[WireCodec[A]].decode(result)

  private def unit(operation: String, args: js.Any*): Unit = call[Unit](operation, args*)

  /**
    * Harvests the entity under the drone. An entity that cannot be harvested is destroyed.
    *
    * @return `true` if an entity was removed, otherwise `false`.
    * @note Costs 200 native ticks when an entity is removed, otherwise 1.
    * @example if (can_harvest()) harvest()
    */
  def harvest(): Boolean = call[Boolean]("harvest")

  /**
    * Checks whether the entity under the drone is ready to harvest.
    *
    * @return `true` for a harvestable entity, otherwise `false`.
    * @note Costs 1 native tick.
    */
  def can_harvest(): Boolean = call[Boolean]("can_harvest")

  /**
    * Swaps the entity under the drone with its neighbor. Some entities cannot be swapped;
    * either tile may be empty.
    *
    * @param direction the neighboring tile: `North`, `East`, `South`, or `West`.
    * @return `true` if the swap succeeds, otherwise `false`.
    * @note Costs 200 native ticks on success, otherwise 1.
    * @example swap(North)
    */
  def swap(direction: Direction): Boolean = call[Boolean]("swap", wire(direction))

  /**
    * Pays the planting cost and plants an entity under the drone. Planting fails when
    * the tile is occupied, the ground is unsuitable, or the required items are unavailable.
    *
    * @param entity the type to plant, such as `Entities.Carrot`.
    * @return `true` if planting succeeds, otherwise `false`.
    * @note Costs 200 native ticks on success, otherwise 1.
    * @example plant(Entities.Bush)
    */
  def plant(entity: Entities): Boolean = call[Boolean]("plant", wire(entity))

  /**
    * Moves the drone one tile. Crossing an ordinary farm edge wraps to the opposite side.
    *
    * @param direction `North` (up), `East` (right), `South` (down), or `West` (left).
    * @return `true` if the drone moves, otherwise `false`.
    * @note Costs 200 native ticks on success, otherwise 1.
    * @example move(North)
    */
  def move(direction: Direction): Boolean = call[Boolean]("move", wire(direction))

  /**
    * Checks whether the drone can move to a neighboring tile.
    *
    * @param direction the direction to test: `North`, `East`, `South`, or `West`.
    * @return `true` if movement is possible, otherwise `false`.
    * @note Costs 1 native tick.
    * @example if (can_move(North)) move(North)
    */
  def can_move(direction: Direction): Boolean = call[Boolean]("can_move", wire(direction))

  /**
    * Switches the ground under the drone between `Grounds.Soil` and `Grounds.Grassland`.
    *
    * @note Costs 200 native ticks. A `Unit` result carries no value.
    */
  def till(): Unit = unit("till")

  /**
    * Reads the drone's horizontal position. Coordinates begin at 0 in the west.
    *
    * @return the current x coordinate, increasing toward the east.
    * @note Costs 1 native tick.
    */
  def get_pos_x(): Int = call[Int]("get_pos_x")

  /**
    * Reads the drone's vertical position. Coordinates begin at 0 in the south.
    *
    * @return the current y coordinate, increasing toward the north.
    * @note Costs 1 native tick.
    */
  def get_pos_y(): Int = call[Int]("get_pos_y")

  /**
    * Reads the current size of the farm.
    *
    * @return the north-to-south side length of the grid.
    * @note Costs 1 native tick.
    * @example for (_ <- 0 until get_world_size()) move(North)
    */
  def get_world_size(): Int = call[Int]("get_world_size")

  /**
    * Reads the entity under the drone.
    *
    * @return `Some(entity)` for an occupied tile, or `None` for an empty tile.
    * @note Costs 1 native tick.
    * @example if (get_entity_type().contains(Entities.Grass)) harvest()
    */
  def get_entity_type(): Option[Entities] = call[Option[Entities]]("get_entity_type")

  /**
    * Reads the ground under the drone.
    *
    * @return the current ground type, such as `Grounds.Soil`.
    * @note Costs 1 native tick.
    * @example if (get_ground_type() != Grounds.Soil) till()
    */
  def get_ground_type(): Grounds = call[Grounds]("get_ground_type")

  /**
    * Reads the current game time.
    *
    * @return seconds elapsed since the game started.
    * @note Costs 0 native ticks.
    */
  def get_time(): Double = call[Double]("get_time")

  /**
    * Reads the native execution tick counter.
    *
    * @return ticks performed since execution started; this does not count Scala-only calculations.
    * @note Costs 0 native ticks.
    */
  def get_tick_count(): Double = call[Double]("get_tick_count")

  /**
    * Attempts to use an item, such as water or fertilizer, on the current tile.
    *
    * @param item the item to use, such as `Items.Water` or `Items.Fertilizer`.
    * @param n how many times to use it; the game converts this to a positive integer. Defaults to 1.
    * @return `true` if an item was used, otherwise `false`.
    * @note Costs 200 native ticks on success, otherwise 1.
    * @example use_item(Items.Fertilizer)
    */
  def use_item(item: Items, n: Double = 1): Boolean = call[Boolean]("use_item", wire(item), wire(n))

  /**
    * Reads the water level under the drone.
    *
    * @return a water level between 0.0 and 1.0.
    * @note Costs 1 native tick.
    * @example if (get_water() < 0.5) use_item(Items.Water)
    */
  def get_water(): Double = call[Double]("get_water")

  /**
    * Makes the drone do a flip.
    *
    * @note Takes one second and is unaffected by speed upgrades.
    */
  def do_a_flip(): Unit = unit("do_a_flip")

  /**
    * Changes the drone's hat.
    *
    * @param hat the hat to wear, such as `Hats.Straw_Hat`.
    */
  def change_hat(hat: Hats): Unit = unit("change_hat", wire(hat))

  /**
    * Displays values in smoke above the drone, separated by spaces.
    *
    * @param values one or more Scala values; each is converted with `String.valueOf`.
    * @note Takes one second and is unaffected by speed upgrades. `game_print` is an explicit alias.
    * @example game_print("ground:", get_ground_type())
    */
  def print(values: Any*): Unit = unit("print", values.map(v => wire(String.valueOf(v)))*)

  /**
    * Displays values in smoke above the drone; an explicit alias for the game `print` function.
    *
    * @param values one or more Scala values to display, separated by spaces.
    * @note Takes one second and is unaffected by speed upgrades.
    * @example game_print("ground:", get_ground_type())
    */
  def game_print(values: Any*): Unit = print(values*)

  /**
    * Writes values to the game's output window without the smoke-print animation.
    *
    * @param values one or more Scala values to print, separated by spaces.
    * @note Costs 0 native ticks.
    * @example quick_print("position:", get_pos_x(), get_pos_y())
    */
  def quick_print(values: Any*): Unit = unit("quick_print", values.map(v => wire(String.valueOf(v)))*)

  /**
    * Reads how much of an item is currently in the inventory.
    *
    * @param item the item to count.
    * @return the amount owned as a `Double`.
    * @note Costs 1 native tick.
    */
  def num_items(item: Items): Double = call[Double]("num_items", wire(item))

  /**
    * Reads an entity's planting cost or an unlock's next upgrade cost.
    *
    * @param thing the entity to plant or the unlock to research.
    * @return `Some(cost)` mapping each required item to its amount, or `None` if no cost is available.
    *   An upgrade already at its maximum level has `Some(Map.empty)`.
    * @note Costs 1 native tick.
    * @example get_cost(Unlocks.Carrots).foreach(cost => cost.foreach((item, amount) => quick_print(item, amount)))
    */
  def get_cost(thing: Entities | Unlocks): Option[Map[Items, Double]] =
    call[Option[Map[Items, Double]]]("get_cost", thing.asInstanceOf[GameValue].wire)

  /**
    * Reads the price of a specified unlock level.
    *
    * @param thing the unlock whose cost should be inspected.
    * @param level the upgrade level to query, using the game's level numbering.
    * @return `Some(cost)` mapping required items to amounts, or `None` when no cost is available.
    * @note Costs 1 native tick.
    */
  def get_cost(thing: Unlocks, level: Double): Option[Map[Items, Double]] =
    call[Option[Map[Items, Double]]]("get_cost", wire(thing), wire(level))

  /**
    * Clears the farm, returns the drone to (0, 0), and restores the straw hat.
    *
    * @note Costs 200 native ticks.
    */
  def clear(): Unit = unit("clear")

  /**
    * Reads the preferred companion of the plant under the drone.
    *
    * @return `Some((entity, (x, y)))` describing the desired companion and its location,
    *   or `None` when the current plant has no companion request.
    * @note Costs 1 native tick.
    * @example get_companion().foreach { case (entity, (x, y)) => quick_print(entity, x, y) }
    */
  def get_companion(): Option[(Entities, (Int, Int))] = call[Option[(Entities, (Int, Int))]]("get_companion")

  /**
    * Purchases an unlock or upgrade, as its research-tree button would.
    *
    * @param unlock the research to purchase. Leaderboard research is disabled in Scala saves.
    * @return `true` if the purchase succeeds, otherwise `false`.
    * @note Costs 200 native ticks on success, otherwise 1. Programming upgrades are pre-unlocked.
    */
  def unlock(unlock: Unlocks): Boolean = call[Boolean]("unlock", wire(unlock))

  /**
    * Checks whether an item, entity, hat, or research upgrade is unlocked.
    *
    * @param thing the item, entity, hat, or unlock to inspect.
    * @return 0 when locked; otherwise 1 plus the number of upgrades for an upgradeable thing,
    *   or 1 for a non-upgradeable unlocked thing.
    * @note Costs 1 native tick.
    */
  def num_unlocked(thing: Entities | Items | Hats | Unlocks): Int =
    call[Int]("num_unlocked", thing.asInstanceOf[GameValue].wire)

  /**
    * Measures the current entity or a neighboring entity. The meaning depends on its type:
    * sunflower petals, cactus size, a pumpkin value, or a treasure/apple target coordinate.
    *
    * @param direction `None` to measure the current tile, or `Some(direction)` to measure a neighbor.
    * @return `Some(number)` or `Some((x, y))` for a measurable entity, otherwise `None`.
    * @note Costs 1 native tick. Pattern matching distinguishes numeric and coordinate results.
    * @example measure(Some(North)).foreach(value => quick_print(value))
    */
  def measure(direction: Option[Direction] = None): Option[Double | (Int, Int)] =
    val raw = js.JSON.parse(Action("measure", js.JSON.stringify(js.Array(direction.fold[js.Any](null)(_.wire)))))
    if raw == null then None
    else if js.typeOf(raw) == "number" then Some(raw.asInstanceOf[Double])
    else Some(summon[WireCodec[(Int, Int)]].decode(raw))

  /**
    * Draws a random number from the game's random generator.
    *
    * @return a `Double` at least 0.0 and less than 1.0.
    */
  def random(): Double = call[Double]("random")

  /**
    * Limits execution speed so farm actions are easier to watch. The limit ends when execution stops.
    *
    * @param speed the speed relative to a drone without speed upgrades; 1 is base speed and 8 is eight times base.
    *   A non-positive value restores maximum speed. Values above the available speed use the available maximum.
    * @note Costs 200 native ticks.
    */
  def set_execution_speed(speed: Double): Unit = unit("set_execution_speed", wire(speed))

  /**
    * Temporarily limits the farm to a square grid, clears it, and resets the drone position.
    * The full farm size is restored when execution stops.
    *
    * @param size the side length; the minimum limited size is 3. A value below 3 restores the full size.
    * @note Costs 200 native ticks.
    */
  def set_world_size(size: Int): Unit = unit("set_world_size", wire(size))

  /**
    * Counts the drones currently on the farm, including the calling drone.
    *
    * @return the number of active drones.
    * @note Costs 1 native tick.
    */
  def num_drones(): Int = call[Int]("num_drones")

  /**
    * Reads the current drone limit.
    *
    * @return the maximum number of drones that can be present on the farm.
    * @note Costs 1 native tick.
    */
  def max_drones(): Int = call[Int]("max_drones")

  /**
    * Starts a drone at the calling drone's position. It executes the callback and disappears when done.
    * Captured Scala values are reconstructed in the child runtime; the child has its own mutable state.
    *
    * @tparam A the callback's result type, with a `WireCodec` for transfer back to the parent.
    * @param function a zero-argument callback, such as `() => harvest()`.
    * @return `Some(handle)` for a new drone, or `None` if the farm's drone limit has been reached.
    * @note Costs 200 native ticks on success, otherwise 1.
    * @example spawn_drone(() => get_pos_x()).foreach(drone => quick_print(wait_for(drone)))
    */
  def spawn_drone[A: WireCodec](function: () => A): Option[Drone[A]] =
    val callback: js.Function0[String] = () => js.JSON.stringify(summon[WireCodec[A]].encode(function()))
    val result = js.JSON.parse(Spawn(callback))
    if result == null then None else Some(Drone[A](WireCodec.obj(result).id.asInstanceOf[String]))

  /**
    * Waits until a drone's callback finishes and reads its typed result.
    *
    * @tparam A the drone callback's result type, with a matching `WireCodec`.
    * @param drone a handle obtained from `spawn_drone`.
    * @return the callback's result, decoded as `A`.
    * @note Costs 1 native tick if the drone has already finished; otherwise waits for completion.
    */
  def wait_for[A: WireCodec](drone: Drone[A]): A = call[A]("wait_for", drone.wire)

  /**
    * Checks whether a drone's callback has completed.
    *
    * @param drone a handle obtained from `spawn_drone`.
    * @return `true` if the drone has finished, otherwise `false`.
    * @note Costs 1 native tick.
    */
  def has_finished(drone: Drone[?]): Boolean = call[Boolean]("has_finished", drone.wire)

  /**
    * Pets the piggy.
    *
    * @note Takes one second and is unaffected by speed upgrades.
    */
  def pet_the_piggy(): Unit = unit("pet_the_piggy")

  /**
    * Sends a tap to the game's optional Tap Tap Loot integration.
    *
    * @note Availability and behavior depend on the native game integration.
    */
  def tap(): Unit = unit("tap")

  /**
    * Simulation values are explicit typed parameters, not dynamically injected
    * identifiers. Supply the same names through sim_globals and read them here.
    *
    * @tparam A the parameter's expected type, with a `WireCodec`.
    * @param name the key supplied in the simulation's globals.
    * @param default the fallback value, evaluated only if the key is absent.
    * @return the supplied value decoded as `A`, or the fallback when absent.
    * @example val rows = simulation_global[Int]("rows", 5)
    */
  def simulation_global[A: WireCodec](name: String, default: => A): A =
    val value = Globals.asInstanceOf[js.Dictionary[js.Any]].get(name)
    value.fold(default)(summon[WireCodec[A]].decode)

  /**
    * Runs a local simulation from a Scala source window, using explicitly supplied starting state.
    * This overload accepts simulation globals whose values all share the same type.
    *
    * @tparam A the globals' value type, with a `WireCodec`.
    * @param filename the source window's module name, such as `"f1"`.
    * @param sim_unlocks starting research mapped to its desired level.
    * @param sim_items starting inventory mapped from items to amounts.
    * @param sim_globals named values read by `simulation_global[A]` inside the simulation.
    * @param seed the non-negative random seed.
    * @param speedup the starting simulation speed multiplier.
    * @return the simulated run's elapsed time in seconds.
    * @note Costs 200 native ticks to start. Call from the main execution. Official leaderboards remain disabled.
    */
  def simulate[A: WireCodec](
    filename: String,
    sim_unlocks: Map[Unlocks, Int],
    sim_items: Map[Items, Double],
    sim_globals: Map[String, A],
    seed: Int,
    speedup: Double
  ): Double =
    simulate(filename, sim_unlocks, sim_items, SimulationGlobals.fromMap(sim_globals), seed, speedup)

  /**
    * Runs a local simulation with a heterogeneous, typed bag of globals.
    *
    * @param filename the source window's module name, such as `"f1"`.
    * @param sim_unlocks starting research mapped to its desired level.
    * @param sim_items starting inventory mapped from items to amounts.
    * @param sim_globals named values built with `SimulationGlobals.empty.updated(name, value)`.
    * @param seed the non-negative random seed.
    * @param speedup the starting simulation speed multiplier.
    * @return the simulated run's elapsed time in seconds.
    * @note Costs 200 native ticks to start. Read globals with `simulation_global[A]`; call from the main execution.
    */
  def simulate(
    filename: String,
    sim_unlocks: Map[Unlocks, Int],
    sim_items: Map[Items, Double],
    sim_globals: SimulationGlobals,
    seed: Int,
    speedup: Double
  ): Double =
    call[Double]("simulate", wire(filename), wire(sim_unlocks), wire(sim_items), sim_globals.wire, wire(seed), wire(speedup))

  /**
    * Runs a local simulation with selected research at its native maximum levels.
    *
    * @param filename the source window's module name, such as `"f1"`.
    * @param sim_unlocks the research to include, each at its native maximum level.
    * @param sim_items starting inventory mapped from items to amounts.
    * @param sim_globals named values built with `SimulationGlobals.empty.updated(name, value)`.
    * @param seed the non-negative random seed.
    * @param speedup the starting simulation speed multiplier.
    * @return the simulated run's elapsed time in seconds.
    * @note Costs 200 native ticks to start. Call from the main execution. Official leaderboards remain disabled.
    */
  def simulate(
    filename: String,
    sim_unlocks: Seq[Unlocks],
    sim_items: Map[Items, Double],
    sim_globals: SimulationGlobals,
    seed: Int,
    speedup: Double
  ): Double =
    call[Double]("simulate", wire(filename), wire(sim_unlocks), wire(sim_items), sim_globals.wire, wire(seed), wire(speedup))

/**
  * A heterogeneous, immutable simulation parameter bag. Each value is decoded
  * with simulation_global[A](name, default) inside the simulated source window.
  */
final class SimulationGlobals private (private[game] val entries: Vector[(String, js.Any)]):
  def updated[A: WireCodec](name: String, value: A): SimulationGlobals =
    new SimulationGlobals(entries.filterNot(_._1 == name) :+ (name -> summon[WireCodec[A]].encode(value)))

  private[game] def wire: js.Any =
    val values = entries.map { (name, value) =>
      js.Array(name.asInstanceOf[js.Any], js.Dynamic.literal("$" -> "scala", "value" -> value)).asInstanceOf[js.Any]
    }
    WireCodec.tagged("dict", js.Array(values*))

object SimulationGlobals:
  val empty: SimulationGlobals = new SimulationGlobals(Vector.empty)

  def fromMap[A: WireCodec](values: Map[String, A]): SimulationGlobals =
    values.foldLeft(empty) { case (parameters, (name, value)) => parameters.updated(name, value) }

final case class Drone[A] private[game] (id: String):
  private[game] def wire: js.Any = js.Dynamic.literal("$" -> "drone", "id" -> id)

trait GameValue:
  def family: String

  def name: String

  private[game] def wire: js.Any = js.Dynamic.literal("$" -> "enum", "family" -> family, "name" -> name)

object GameValue:
  private[game] def codec[A <: GameValue](family: String, decodeName: String => A): WireCodec[A] = new WireCodec[A]:
    def encode(value: A): js.Any = value.wire

    def decode(value: js.Any): A =
      require(WireCodec.obj(value).family.asInstanceOf[String] == family, "Game enum family mismatch")
      decodeName(WireCodec.obj(value).name.asInstanceOf[String])
