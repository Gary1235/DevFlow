const { app, BrowserWindow, Menu, Tray, nativeImage, dialog, shell, Notification } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const { Services, taskNotifications, isSameOrigin } = require('./services.cjs');
const { Updates, hasActiveWork } = require('./updates.cjs');

app.setName('DevFlow');
app.setAppUserModelId('dev.devflow.desktop');
let window, tray, services, timer, updates, updateTimer;
let ready = false, quitting = false, confirming = false, failureShown = false;
let previous;
let previousPermissions = new Set();
const smoke = process.argv.includes('--smoke-test');
if (smoke) app.setPath('userData', fs.mkdtempSync(path.join(os.tmpdir(), 'devflow-smoke-profile-')));
const icon = nativeImage.createFromPath(path.join(__dirname, 'icon.png'));

function show() {
  if (!window || window.isDestroyed()) return;
  if (window.isMinimized()) window.restore();
  window.show();
  window.focus();
}
async function stopServices() {
  while (true) {
    try { await services?.stop(); return; }
    catch (error) {
      const result = await dialog.showMessageBox({ type: 'warning', title: '服務仍在停止中',
        message: error.message, detail: '建議繼續等待，讓測試與 AI 子程序完成清理。強制停止可能留下未完成紀錄，重新啟動後會進行中斷恢復。',
        buttons: ['繼續等待', '強制停止'], defaultId: 0, cancelId: 0 });
      if (result.response === 1) {
        try { await services.forceStop(); return; }
        catch (failure) { dialog.showErrorBox('停止失敗', failure.message); }
      }
    }
  }
}
async function quit() {
  if (quitting || confirming) return;
  confirming = true;
  const result = await dialog.showMessageBox({ type: 'question', title: '結束 DevFlow',
    message: '要停止背景服務並結束 DevFlow 嗎？',
    detail: '執行中的規劃、實作與討論會中斷；CLI 更新也可能受影響。要讓任務繼續執行，請選擇留在背景。排隊與歷史資料會保留。',
    buttons: ['留在背景', '停止服務並結束'], defaultId: 0, cancelId: 0 });
  confirming = false;
  if (result.response !== 1) { window?.hide(); return; }
  quitting = true;
  clearInterval(timer);
  clearInterval(updateTimer);
  window?.hide();
  await stopServices();
  tray?.destroy();
  app.exit(0);
}
function notifyUpdate(title, body) {
  if (!Notification.isSupported()) return;
  const notification = new Notification({ title, body, icon });
  notification.on('click', show);
  notification.show();
}
let recoveringUpdate = false;
async function recoverUpdate(error) {
  if (recoveringUpdate) return;
  recoveringUpdate = true;
  dialog.showErrorBox('無法安裝 DevFlow 更新', `${error.message}\n\nDevFlow 將重新啟動，稍後可再次檢查更新。`);
  app.relaunch();
  app.exit(1);
}
async function restartForUpdate() {
  if (quitting || confirming || !ready) return;
  await updates.install({
    confirm: async () => {
      confirming = true;
      try {
        const result = await dialog.showMessageBox({ type: 'question', title: '更新 DevFlow',
          message: `重新啟動並安裝 DevFlow ${updates.version}？`,
          detail: '請先完成 CLI 更新與登入作業。系統會再次檢查任務與討論，停止背景服務後安裝更新；排隊與歷史資料會保留。',
          buttons: ['稍後', '重新啟動以更新'], defaultId: 0, cancelId: 0 });
        if (result.response !== 1) return false;
        const [tasks, discussions] = await Promise.all([
          services.request('/api/tasks').then(response => response.json()),
          services.request('/api/discussions').then(response => response.json())
        ]);
        if (hasActiveWork(tasks, discussions)) {
          await dialog.showMessageBox({ type: 'info', title: '工作仍在執行',
            message: '請等待任務與討論完成後，再選擇重新啟動以更新。' });
          return false;
        }
        return true;
      } finally { confirming = false; }
    },
    stop: async () => {
      quitting = true;
      clearInterval(timer);
      clearInterval(updateTimer);
      window?.hide();
      await stopServices();
    },
    recover: async error => {
      if (quitting) await recoverUpdate(error);
      else dialog.showErrorBox('暫時無法更新', `${error.message}\n請稍後重試。`);
    }
  });
}
function updateMenuItem() {
  return { label: updates.label, enabled: updates.actionable,
    click: () => { void (updates.state === 'downloaded' ? restartForUpdate() : updates.check(true)); } };
}
function refreshMenus() {
  if (!tray || !updates || quitting) return;
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: '開啟 DevFlow', click: show },
    { label: '開啟資料目錄', click: () => { void shell.openPath(services.dataRoot); } },
    { label: '開啟診斷紀錄', click: () => { void shell.openPath(path.join(services.dataRoot, 'logs')); } },
    { type: 'separator' }, updateMenuItem(),
    { type: 'separator' }, { label: '停止服務並結束', click: () => { void quit(); } }
  ]));
  Menu.setApplicationMenu(Menu.buildFromTemplate([
    { label: 'DevFlow', submenu: [{ label: `版本 ${app.getVersion()}`, enabled: false }, updateMenuItem(),
      { type: 'separator' }, { label: '隱藏至系統匣', click: () => window.hide() },
      { label: '停止服務並結束', click: () => { void quit(); } }] },
    { label: '編輯', submenu: [{ role: 'undo' }, { role: 'redo' }, { type: 'separator' }, { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }] },
    { label: '檢視', submenu: [{ role: 'reload' }, { role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' }, { role: 'togglefullscreen' }] }
  ]));
}
async function fail(message) {
  if (smoke) {
    console.error(message);
    quitting = true;
    try { await services?.stop(); } catch (error) { console.error(error.message); }
    app.exit(1);
    return;
  }
  if (failureShown || quitting) return;
  failureShown = true;
  ready = false;
  clearInterval(timer);
  clearInterval(updateTimer);
  dialog.showErrorBox('DevFlow 服務無法執行', `${message}\n\n診斷紀錄：${path.join(services.dataRoot, 'logs')}\n服務會停止，資料會保留；修正問題後重新開啟。`);
  quitting = true;
  await stopServices();
  tray?.destroy();
  app.exit(1);
}
async function poll() {
  if (!ready || quitting) return;
  try {
    const tasks = await (await services.request('/api/tasks')).json();
    const labels = { AwaitingApproval: '計畫等待核准', AwaitingReview: '成果等待審核', AwaitingStepApproval: '流程步驟等待確認', Failed: '任務執行失敗', PlanningFailed: '任務規劃失敗', Merged: '成果已合併' };
    for (const task of taskNotifications(previous, tasks)) {
      if (!Notification.isSupported()) break;
      const notification = new Notification({ title: labels[task.status], body: task.title, icon });
      notification.on('click', () => {
        show();
        if (!window.isDestroyed()) void window.loadURL(`${services.origin}/#task/${encodeURIComponent(task.id)}`);
      });
      notification.show();
    }
    previous = new Map(tasks.map(task => [task.id, task.status]));
    const pending = await (await services.request('/api/permissions/pending')).json();
    for (const request of pending) {
      if (previousPermissions.has(request.id) || !Notification.isSupported()) continue;
      const notification = new Notification({ title: '工具等待核准', body: request.title + ' · ' + request.reason, icon });
      notification.on('click', () => {
        show();
        if (!window.isDestroyed()) void window.loadURL(`${services.origin}/#${request.kind}/${encodeURIComponent(request.ownerId)}`);
      });
      notification.show();
    }
    previousPermissions = new Set(pending.map(p => p.id));
  } catch { /* A transient request failure must not restart or duplicate services. */ }
}

if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', show);
  app.on('activate', show);
  app.on('window-all-closed', () => {});
  app.on('before-quit', event => {
    if (!quitting) { event.preventDefault(); void quit(); }
  });
  app.whenReady().then(async () => {
    const dataRoot = smoke ? fs.mkdtempSync(path.join(os.tmpdir(), 'devflow-smoke-')) : process.env.DEVFLOW_DATA_DIR
      ? path.resolve(process.env.DEVFLOW_DATA_DIR) : path.join(app.getPath('userData'), 'data');
    services = new Services({ payload: app.isPackaged ? path.join(process.resourcesPath, 'backend') : path.resolve(__dirname, '../payload'), dataRoot });
    services.on('service-exit', ({ name, code }) => {
      if (ready) void fail(`${name} 意外結束（${code ?? 'unknown'}）。`);
    });
    try {
      await services.start();
      window = new BrowserWindow({ width: 1440, height: 940, minWidth: 1000, minHeight: 680,
        title: 'DevFlow', icon, show: false, backgroundColor: '#f5f7fb',
        webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true, partition: 'devflow-desktop' } });
      const session = window.webContents.session;
      session.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
      session.setPermissionCheckHandler(() => false);
      session.webRequest.onBeforeSendHeaders({ urls: [services.origin + '/*'] }, (details, callback) => {
        callback({ requestHeaders: { ...details.requestHeaders, 'X-DevFlow-Desktop': services.token } });
      });
      session.webRequest.onHeadersReceived({ urls: [services.origin + '/*'] }, (details, callback) => {
        callback({ responseHeaders: { ...details.responseHeaders,
          'Content-Security-Policy': ["default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-src 'none'; base-uri 'self'; form-action 'self'"] } });
      });
      window.webContents.on('will-attach-webview', event => event.preventDefault());
      window.webContents.on('will-navigate', (event, url) => {
        if (!isSameOrigin(url, services.origin)) event.preventDefault();
      });
      window.webContents.on('will-redirect', (event, url) => {
        if (!isSameOrigin(url, services.origin)) event.preventDefault();
      });
      window.webContents.setWindowOpenHandler(({ url }) => {
        if (/^https?:\/\//i.test(url) && !isSameOrigin(url, services.origin)) void shell.openExternal(url);
        return { action: 'deny' };
      });
      window.webContents.on('render-process-gone', () => { if (!quitting) window.reload(); });
      window.on('close', event => { if (!quitting) { event.preventDefault(); window.hide(); } });
      window.on('page-title-updated', event => event.preventDefault());
      tray = new Tray(icon);
      tray.setToolTip('DevFlow — 背景任務持續執行');
      updates = new Updates({ updater: require('electron-updater').autoUpdater,
        enabled: app.isPackaged && !smoke, notify: notifyUpdate });
      updates.on('changed', refreshMenus);
      updates.on('update-error', error => { if (quitting && updates.installing) void recoverUpdate(error); });
      refreshMenus();
      tray.on('double-click', show);
      await window.loadURL(services.origin);
      if (services.children.some(child => child.exited)) throw new Error('服務在載入期間結束。');
      ready = true;
      show();
      if (smoke) {
        // Real Electron renderer + real .NET services; no model calls or user data.
        const connected = await window.webContents.executeJavaScript(`new Promise(resolve => {
          const deadline = Date.now() + 45000;
          const timer = setInterval(() => {
            if (document.body.innerText.includes('API 已連線')) { clearInterval(timer); resolve(true); }
            else if (Date.now() > deadline) { clearInterval(timer); resolve(false); }
          }, 100);
        })`);
        if (!connected) throw new Error('React 工作台未能連上 API。');
        const text = await window.webContents.executeJavaScript('document.body.innerText');
        if (!text.includes('開發總覽') || text.includes('讀取工作空間')) throw new Error('React 工作台沒有成功顯示。');
        if (await window.webContents.executeJavaScript('typeof require') !== 'undefined') throw new Error('Renderer Node isolation failed.');
        if ((await fetch(services.origin + '/health')).status !== 401) throw new Error('Unauthenticated API access was allowed.');
        const projects = await (await services.request('/api/projects')).json();
        if (!Array.isArray(projects) || projects.length) throw new Error('Smoke session must use an empty isolated database.');
        window.close();
        if (window.isDestroyed() || window.isVisible()) throw new Error('Close-to-tray failed.');
        await services.request('/health');
        show();
        const target = app.isPackaged ? path.join(path.dirname(app.getPath('exe')), 'smoke.png') : path.resolve(__dirname, '../release/smoke.png');
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.writeFileSync(target, (await window.webContents.capturePage()).toPNG());
        quitting = true;
        await services.stop();
        if (services.children.some(child => !child.exited)) throw new Error('Child services survived shutdown.');
        tray.destroy();
        fs.rmSync(dataRoot, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 });
        console.log('PASS: React, API authentication, Worker readiness, close-to-tray, graceful shutdown. Screenshot: ' + target);
        app.exit(0);
        return;
      }
      await poll();
      timer = setInterval(() => { void poll(); }, 5000);
      void updates.check();
      if (updates.enabled) updateTimer = setInterval(() => { void updates.check(); }, 4 * 60 * 60 * 1000);
    } catch (error) { await fail(error.message); }
  }).catch(error => { dialog.showErrorBox('DevFlow 啟動失敗', error.message); app.exit(1); });
}
