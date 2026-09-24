using System.Runtime.CompilerServices;

// The detector tests age trackers the way tests-detect.lisp does (decf of the
// tracker start time), through the internal Detector.AgeTrackers hook.
[assembly: InternalsVisibleTo("RappyRuns.Tests")]
