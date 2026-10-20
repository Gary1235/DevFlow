const { test } = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { Updates, hasActiveWork } = require('../src/updates.cjs');

function setup(enabled = true) {
  const updater = new EventEmitter();
  const notifications = [];
  const updates = new Updates({ updater, enabled,
    notify: (...args) => notifications.push(args), log: { warn() {} } });
  return { updater, updates, notifications };
}

test('development and smoke mode never query the update server or install', async () => {
  const { updates, updater } = setup(false);
  updater.checkForUpdates = () => assert.fail('unexpected update request');
  updater.quitAndInstall = () => assert.fail('unexpected installation');
  await updates.check(true);
  updates.setState('downloaded');
  await updates.install({});
  assert.equal(updates.actionable, false);
});

test('background download does not install on ordinary app exit; restart stops services first', async () => {
  const { updates, updater, notifications } = setup();
  assert.equal(updater.autoInstallOnAppQuit, false);
  assert.equal(updater.autoDownload, true);
  assert.equal(updater.allowDowngrade, false);
  assert.equal(updater.allowPrerelease, false);
  const calls = [];
  updater.checkForUpdates = async () => {
    updater.emit('update-available', { version: '0.1.2' });
    return { downloadPromise: Promise.resolve().then(() => updater.emit('update-downloaded', { version: '0.1.2' })) };
  };
  updater.quitAndInstall = (...args) => calls.push(['install', ...args]);
  await updates.check();
  assert.equal(updates.state, 'downloaded');
  assert.match(updates.label, /0.1.2/);
  assert.equal(notifications.length, 1);
  assert.deepEqual(calls, []);
  let finishStopping;
  const stopping = new Promise(resolve => { finishStopping = resolve; });
  const hooks = { confirm: async () => { calls.push('confirm'); return true; },
    stop: async () => { calls.push('stop'); await stopping; }, recover: assert.fail };
  const pending = updates.install(hooks);
  await new Promise(resolve => setImmediate(resolve));
  await updates.install(hooks); // Double click must not launch two installers.
  assert.deepEqual(calls, ['confirm', 'stop']);
  finishStopping();
  await pending;
  assert.deepEqual(calls, ['confirm', 'stop', ['install', true, true]]);
  assert.equal(updates.actionable, false);
});

test('declining or having active work leaves services running and update ready', async () => {
  const { updates, updater } = setup();
  updater.emit('update-downloaded', { version: '0.1.2' });
  await updates.install({ confirm: async () => false, stop: assert.fail, recover: assert.fail });
  assert.equal(updates.state, 'downloaded');
  assert.equal(updates.actionable, true);
  for (const status of ['Running', 'Planning', 'Merging', 'Queued', 'PlanningQueued'])
    assert.equal(hasActiveWork([{ status }], []), true);
  assert.equal(hasActiveWork([], [{ activeOperation: 'Replying' }]), true);
  assert.equal(hasActiveWork([{ status: 'AwaitingReview' }], [{ activeOperation: null }]), false);
});

test('shutdown errors prevent installation and invoke recovery', async () => {
  const { updates, updater } = setup();
  updater.emit('update-downloaded', { version: '0.1.2' });
  updater.quitAndInstall = assert.fail;
  const error = new Error('Worker still running');
  let recovered;
  await updates.install({ confirm: async () => true, stop: async () => { throw error; },
    recover: async failure => { recovered = failure; } });
  assert.equal(recovered, error);
  assert.equal(updates.actionable, true);
});

test('check and download failures stay quiet in background and can be retried manually', async () => {
  const { updates, updater, notifications } = setup();
  updater.checkForUpdates = async () => { throw new Error('offline'); };
  await updates.check();
  assert.equal(updates.state, 'error');
  assert.equal(notifications.length, 0);
  await updates.check(true);
  assert.equal(notifications.length, 1);
  updater.checkForUpdates = async () => {
    updater.emit('update-available', { version: '0.1.2' });
    return { downloadPromise: Promise.reject(new Error('checksum mismatch')) };
  };
  await updates.check();
  assert.equal(updates.state, 'error');
  updater.checkForUpdates = async () => updater.emit('update-not-available');
  await updates.check(true);
  assert.equal(updates.state, 'idle');
  assert.match(notifications.at(-1)[0], /最新版本/);
});

test('duplicate checks are ignored and installer errors remain observable', async () => {
  const { updates, updater } = setup();
  let finish, checks = 0, error;
  updater.checkForUpdates = () => { checks++; return new Promise(resolve => { finish = resolve; }); };
  const pending = updates.check();
  await updates.check(true);
  assert.equal(checks, 1);
  finish();
  await pending;
  updates.on('update-error', failure => { error = failure; });
  updater.emit('update-downloaded', { version: '0.1.2' });
  updater.quitAndInstall = () => updater.emit('error', new Error('installer unavailable'));
  await updates.install({ confirm: async () => true, stop: async () => {}, recover: assert.fail });
  assert.match(error.message, /installer unavailable/);
});
