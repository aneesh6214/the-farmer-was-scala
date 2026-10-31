package game

/**
  * Cardinal directions used by movement and other directional actions.
  */
enum Directions extends GameValue:
  case East, North, South, West
  // Native bridge tag; the public Scala type is Directions.
  def family: String = "Direction"

  def name: String = toString

object Directions:
  given WireCodec[Directions] = GameValue.codec("Direction", valueOf)

/**
  * Plant and farm entity types used by planting, sensing and cost queries.
  */
enum Entities extends GameValue:
  case Apple, Bush, Cactus, Carrot, Dead_Pumpkin, Dinosaur, Grass, Hedge, Pumpkin, Sunflower
  case Treasure, Tree
  def family: String = "Entities"

  def name: String = toString

object Entities:
  given WireCodec[Entities] = GameValue.codec("Entities", valueOf)

/**
  * Ground types returned by ground sensing.
  */
enum Grounds extends GameValue:
  case Grassland, Soil
  def family: String = "Grounds"

  def name: String = toString

object Grounds:
  given WireCodec[Grounds] = GameValue.codec("Grounds", valueOf)

/**
  * Inventory item types used by costs, item counts and item actions.
  */
enum Items extends GameValue:
  case Bone, Cactus, Carrot, Fertilizer, Gold, Hay, Piggy, Power, Pumpkin, Water, Weird_Substance
  case Wood
  def family: String = "Items"

  def name: String = toString

object Items:
  given WireCodec[Items] = GameValue.codec("Items", valueOf)

/**
  * Upgrade identifiers used by unlock, cost and simulation functions.
  */
enum Unlocks extends GameValue:
  case Auto_Unlock, Cactus, Carrots, Costs, Debug, Debug_2, Dictionaries, Dinosaurs, Expand
  case Fertilizer, Functions, Grass, Hats, Import, Leaderboard, Lists, Loops, Mazes, Megafarm
  case Operators, Plant, Polyculture, Pumpkins, Senses, Simulation, Speed, Sunflowers
  case The_Farmers_Remains, Timing, Top_Hat, Trees, Utilities, Variables, Watering
  def family: String = "Unlocks"

  def name: String = toString

object Unlocks:
  given WireCodec[Unlocks] = GameValue.codec("Unlocks", valueOf)

/**
  * Hat identifiers accepted by change_hat.
  */
enum Hats extends GameValue:
  case Brown_Hat, Cactus_Hat, Carrot_Hat, Dinosaur_Hat, Gold_Hat, Gold_Trophy_Hat, Golden_Cactus_Hat
  case Golden_Carrot_Hat, Golden_Gold_Hat, Golden_Pumpkin_Hat, Golden_Sunflower_Hat, Golden_Tree_Hat
  case Gray_Hat, Green_Hat, Pumpkin_Hat, Purple_Hat, Silver_Trophy_Hat, Straw_Hat, Sunflower_Hat
  case The_Farmers_Remains, Top_Hat, Traffic_Cone, Traffic_Cone_Stack, Tree_Hat, Wizard_Hat
  case Wood_Trophy_Hat
  def family: String = "Hats"

  def name: String = toString

object Hats:
  given WireCodec[Hats] = GameValue.codec("Hats", valueOf)
