const { spawn } = require('node:child_process');
const { EventEmitter } = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const { randomBytes } = require('node:crypto');

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function freePort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const port = server.address().port;
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  return port;
}

class Services extends EventEmitter {
  constructor({ payload, dataRoot, spawnProcess = spawn }) {
    super();
    this.payload = payload;
    this.dataRoot = dataRoot;
    this.spawnProcess = spawnProcess;
    this.children = [];
    this.stopping = false;
    this.token = randomBytes(32).toString('hex');
  }
  async start() {
    fs.mkdirSync(this.dataRoot, { recursive: true });
    fs.mkdirSync(path.join(this.dataRoot, 'logs'), { recursive: true });
    this.origin = `http://127.0.0.1:${await freePort()}`;
    const env = {
      ...process.env,
      DevFlow__DesktopManaged: 'true',
      DevFlow__DatabasePath: path.join(this.dataRoot, 'devflow.db'),
      DevFlow__ArtifactsRoot: path.join(this.dataRoot, 'artifacts'),
      DevFlow__WorktreeRoot: path.join(this.dataRoot, 'worktrees'),
      DevFlow__CliControlRoot: path.join(this.dataRoot, 'cli-control'),
      DOTNET_ENVIRONMENT: 'Production',
      ASPNETCORE_ENVIRONMENT: 'Production'
    };
    // A packaged desktop process must never inherit a development launch URL or secret.
    delete env.DevFlow__DesktopToken;
    const api = this.launch('api', { ...env, DevFlow__ApiUrl: this.origin, DevFlow__DesktopToken: this.token });
    await this.waitForApi(api);
    const worker = this.launch('worker', env);
    const deadline = Date.now() + 30000;
    while (!worker.ready) {
      if (worker.exited) throw new Error('Worker 啟動失敗，請查看 worker.log。');
      if (Date.now() > deadline) throw new Error('Worker 啟動逾時，請查看 worker.log。');
      await delay(100);
    }
    return this.origin;
  }
  launch(name, env) {
    const executable = path.join(this.payload, name, `DevFlow.${name === 'api' ? 'Api' : 'Worker'}.exe`);
    if (!fs.existsSync(executable)) throw new Error(`找不到 ${name}，請先執行 npm run prepare:backend。`);
    const child = this.spawnProcess(executable, [], {
      cwd: path.dirname(executable), env, windowsHide: true, shell: false,
      stdio: ['pipe', 'pipe', 'pipe']
    });
    const entry = { name, child, exited: false, ready: false, error: null };
    this.children.push(entry);
    const log = fs.createWriteStream(path.join(this.dataRoot, 'logs', `${name}.log`), { flags: 'w' });
    log.on('error', () => {});
    let bytes = 0;
    let tail = '';
    const capture = chunk => {
      // Keep draining output even after the bounded diagnostic log is full.
      if (bytes < 4 * 1024 * 1024) {
        const part = chunk.subarray(0, 4 * 1024 * 1024 - bytes);
        bytes += part.length;
        log.write(part);
      }
      tail = (tail + chunk.toString()).slice(-1024);
      if (tail.includes('DevFlow Worker started.')) entry.ready = true;
    };
    child.stdout.on('data', capture);
    child.stderr.on('data', capture);
    child.stdin.on('error', () => {});
    child.once('error', error => { entry.error = error; });
    child.once('close', code => {
      entry.exited = true;
      log.end();
      if (!this.stopping) this.emit('service-exit', { name, code });
    });
    return entry;
  }
  async request(route) {
    const response = await fetch(this.origin + route, {
      headers: { 'X-DevFlow-Desktop': this.token }, signal: AbortSignal.timeout(3000)
    });
    if (!response.ok) throw new Error(`Local API returned ${response.status}`);
    return response;
  }
  async waitForApi(entry) {
    const deadline = Date.now() + 45000;
    while (Date.now() < deadline) {
      if (entry.exited || entry.error) throw new Error('API 啟動失敗，請查看 api.log。');
      try {
        const response = await this.request('/health');
        if ((await response.json()).status === 'ok') return;
      } catch { /* Initialization or binding still in progress. */ }
      await delay(200);
    }
    throw new Error('API 啟動逾時，請查看 api.log。');
  }
  async stop(timeoutMs = 30000) {
    this.stopping = true;
    // Let Worker cancel child CLI/test processes and finish evidence before API stops.
    for (const entry of [...this.children].reverse()) {
      if (entry.exited) continue;
      if (!entry.child.stdin.destroyed && !entry.stopSent) {
        entry.stopSent = true;
        entry.child.stdin.end('shutdown\n');
      }
      const deadline = Date.now() + timeoutMs;
      while (!entry.exited && Date.now() < deadline) await delay(100);
      if (!entry.exited) throw new Error(`${entry.name} 尚未停止。`);
    }
  }
  async forceStop() {
    this.stopping = true;
    for (const entry of [...this.children].reverse()) {
      if (entry.exited || !entry.child.pid) continue;
      // Only terminate a process tree created and still owned by this launcher.
      await new Promise((resolve, reject) => {
        const killer = this.spawnProcess(path.join(process.env.SystemRoot || 'C:\\Windows', 'System32/taskkill.exe'),
          ['/PID', String(entry.child.pid), '/T', '/F'], { windowsHide: true, shell: false });
        killer.once('error', reject);
        killer.once('close', code => code === 0 || entry.exited ? resolve() : reject(new Error('無法停止服務，請查看工作管理員。')));
      });
      const deadline = Date.now() + 5000;
      while (!entry.exited && Date.now() < deadline) await delay(100);
      if (!entry.exited) throw new Error('服務尚未結束，請稍後再試。');
    }
  }
}

function taskNotifications(previous, tasks) {
  if (!previous) return []; // Seed existing history without notifying on launch.
  const watched = new Set(['AwaitingApproval', 'AwaitingReview', 'AwaitingStepApproval', 'Failed', 'PlanningFailed', 'Merged']);
  return tasks.filter(task => previous.get(task.id) !== task.status && watched.has(task.status));
}
function isSameOrigin(url, origin) {
  try { return new URL(url).origin === origin; } catch { return false; }
}
module.exports = { Services, freePort, taskNotifications, isSameOrigin };
