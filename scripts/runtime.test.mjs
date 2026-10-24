// Real API/Worker startup and shutdown; no tasks and no model calls.
process.env.DEVFLOW_TEST_RUNTIME='1';
// A known native executable is sufficient for Worker startup in an empty fixture.
// It is never invoked as an AI CLI by these tests.
process.env.DevFlow__CodexExecutable=process.execPath;
await import('./dev.test.mjs');
