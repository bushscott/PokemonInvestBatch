// Same rule as the integration suite: every test here migrates, Respawn-resets
// and seeds the SAME pokemon_test database on the Pi, so two running at once
// would delete each other's fixtures mid-assertion. One at a time, always.
//
// Note this only serialises within THIS assembly. These tests and
// PokemonInvestBatch.Integration.Tests share one database, so the two suites
// must not be run concurrently either.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
