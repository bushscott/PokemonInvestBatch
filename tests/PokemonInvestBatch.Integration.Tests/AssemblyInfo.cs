// Every class here migrates, Respawn-resets, and seeds the SAME pokemon_test
// database on the Pi — two classes running in parallel corrupt each other's
// fixtures (duplicate pk_sets). One at a time, always.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
