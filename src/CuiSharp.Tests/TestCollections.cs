namespace CuiSharp.Tests;

// The window, the frame loop, FrameStats and PendingWork are process-wide. xUnit runs each
// collection it has no definition for in parallel with every other collection, so a frame drawn
// by another test can call MarkDrawn while a hosting test is asserting what the loop owes. This
// collection owns that shared state and therefore runs on its own.
[CollectionDefinition("ProcessWindowState", DisableParallelization = true)]
public sealed class ProcessWindowStateCollection { }
