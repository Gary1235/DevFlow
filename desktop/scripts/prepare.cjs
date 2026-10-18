const { spawnSync } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');
const root = path.resolve(__dirname, '../..');
if (process.platform !== 'win32') throw new Error('The first desktop release targets Windows x64.');
function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', shell: false });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}
// Run npm through its JS entry point; never interpolate paths into a shell command.
if (!process.env.npm_execpath) throw new Error('Run this script through npm run prepare:backend.');
run(process.execPath, [process.env.npm_execpath, 'run', 'build'], path.join(root, 'frontend'));
for (const name of ['Api', 'Worker']) {
  const target = path.join(root, 'desktop/payload', name.toLowerCase());
  fs.mkdirSync(target, { recursive: true });
  run('dotnet', ['publish', path.join(root, `backend/src/DevFlow.${name}`), '-c', 'Release',
    '-r', 'win-x64', '--self-contained', 'true', '-o', target], path.join(root, 'backend'));
}
if (!fs.existsSync(path.join(root, 'desktop/payload/api/ui/index.html')))
  throw new Error('Published API is missing the React workbench.');
