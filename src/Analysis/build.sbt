ThisBuild / scalaVersion := "3.9.0"
name := "farmer-scala-analysis"
version := "1.1.0"
libraryDependencies += "org.scala-lang" %% "scala3-presentation-compiler" % "3.9.0"
// Pinned direct API: offline, no dynamic downloads, reused by the source index.
libraryDependencies += ("org.scalameta" % "scalafmt-core_2.13" % "3.11.5")
  .exclude("org.scala-lang", "scala-library")
  // Scalameta's macro-build dependencies are not needed for parsing/formatting.
  // Keep the bundled Scala 3 library and avoid shipping another compiler.
  .exclude("org.scala-lang", "scala-compiler")
  .exclude("org.scala-lang", "scala-reflect")
Compile / mainClass := Some("farmer.analysis.Main")
lazy val stageAnalysis = taskKey[File]("Stage the standalone presentation compiler and dependencies")
stageAnalysis := {
  val jar = (Compile / packageBin).value
  val target = baseDirectory.value / ".." / ".." / ".local" / "analysis"
  IO.createDirectory(target)
  val dependencies = (Runtime / dependencyClasspath).value.map(_.data).filter(_.getName.endsWith(".jar"))
  val jars = jar +: dependencies
  // Rebuilds must not accumulate obsolete transitive jars in a distributable stage.
  (target * "*.jar").get.filterNot(f => jars.exists(_.getName == f.getName)).foreach(IO.delete)
  jars.foreach(f => IO.copyFile(f, target / f.getName))
  IO.write(target / "classpath.txt", jars.map(_.getName).mkString("\n") + "\n")
  target
}
