using Xunit;

// Jellyfin resolves an episode's series through BaseItem.LibraryManager, which is
// a static on the entity type — process-global state this suite has to write in
// order to make a real Episode report real keys. xUnit runs test classes in
// parallel, so two classes each standing up their own catalogue were overwriting
// that static underneath one another: an episode would resolve its series through
// the other test's library and report a key set from a show it had never heard of.
//
// It surfaced as roughly one run in five, which is the worst frequency for a
// failure to have. The fix is not per-test cleanup — there is no moment between
// two parallel classes at which either could safely restore the value — but to
// stop the assembly running them at once.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
