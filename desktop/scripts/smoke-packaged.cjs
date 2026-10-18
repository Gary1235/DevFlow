const { spawn } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');
const executable = path.resolve(__dirname, '../release/win-unpacked/DevFlow.exe');
if (!fs.existsSync(executable)) throw new Error('Build the desktop package before testing it.');
const child = spawn(executable, ['--smoke-test'], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true, shell: false });
let passed = false;
let output = '';
child.stdout.on('data', data => {
  output = (output + data.toString()).slice(-8192);
  if (output.includes('PASS: React, API authentication, Worker readiness, close-to-tray, graceful shutdown.')) passed = true;
  process.stdout.write(data);
});
child.stderr.on('data', data => process.stderr.write(data));
child.once('error', error => { console.error(error.message); process.exitCode = 1; });
child.once('close', code => { process.exitCode = code === 0 && passed ? 0 : 1; });
