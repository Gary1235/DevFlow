const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { Services, freePort, taskNotifications, isSameOrigin } = require('../src/services.cjs');

test('initial history is silent; fast new tasks notify and unchanged tasks do not repeat', () => {
  const previous = new Map([['a', 'Running'], ['b', 'AwaitingReview']]);
  const tasks = [
    { id: 'a', status: 'AwaitingReview' }, { id: 'b', status: 'AwaitingReview' },
    { id: 'c', status: 'Failed' }
  ];
  assert.deepEqual(taskNotifications(undefined, tasks), []);
  assert.deepEqual(taskNotifications(previous, tasks).map(task => task.id), ['a', 'c']);
});
test('navigation checks exact scheme, host and port', () => {
  const origin = 'http://127.0.0.1:5180';
  assert.equal(isSameOrigin(origin + '/#tasks', origin), true);
  for (const url of ['http://127.0.0.1:5181', 'https://127.0.0.1:5180', 'http://127.0.0.1:5180.evil.test', 'file:///etc/passwd', 'invalid'])
    assert.equal(isSameOrigin(url, origin), false);
});
test('allocates a loopback port', async () => assert.ok(await freePort() > 0));
test('owned services share data paths, require secret and stop Worker before API', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'devflow-desktop-test-'));
  const fixture = path.join(root, 'fixture.cjs');
  fs.writeFileSync(fixture, `
    const fs = require('node:fs');
    const http = require('node:http');
    const name = process.argv[2];
    let server;
    if (name === 'api') {
      const url = new URL(process.env.DevFlow__ApiUrl);
      server = http.createServer((req, res) => {
        res.statusCode = req.headers['x-devflow-desktop'] === process.env.DevFlow__DesktopToken ? 200 : 401;
        res.end(JSON.stringify({status:'ok'}));
      }).listen(Number(url.port), url.hostname);
    } else console.log('DevFlow Worker started.');
    process.stdin.resume();
    process.stdin.on('end', () => {
      fs.appendFileSync(process.env.DevFlow__DatabasePath + '.order', name + '\\n');
      if (server) server.close(() => process.exit(0)); else process.exit(0);
    });
  `);
  const launched = [];
  for (const name of ['api', 'worker']) {
    fs.mkdirSync(path.join(root, name));
    fs.writeFileSync(path.join(root, name, `DevFlow.${name === 'api' ? 'Api' : 'Worker'}.exe`), 'fixture');
  }
  const services = new Services({ payload: root, dataRoot: path.join(root, 'data with spaces'),
    spawnProcess: (executable, args, options) => {
      launched.push({ executable, options });
      return spawn(process.execPath, [fixture, path.basename(path.dirname(executable))], options);
    }
  });
  try {
    await services.start();
    assert.equal((await fetch(services.origin + '/health')).status, 401);
    assert.equal((await services.request('/health')).status, 200);
    assert.equal(launched[0].options.env.DevFlow__DatabasePath, launched[1].options.env.DevFlow__DatabasePath);
    assert.equal(launched[0].options.env.DevFlow__CliControlRoot, launched[1].options.env.DevFlow__CliControlRoot);
    assert.equal(launched[1].options.env.DevFlow__DesktopToken, undefined);
    assert.equal(launched[0].options.shell, false);
    await services.stop(5000);
    assert.equal(fs.readFileSync(path.join(services.dataRoot, 'devflow.db.order'), 'utf8'), 'worker\napi\n');
    await services.stop(100); // Repeated shutdown must be safe.
    assert.ok(services.children.every(entry => entry.exited));
  } finally {
    await services.stop(5000);
    fs.rmSync(root, { recursive: true, force: true });
  }
});
