const { EventEmitter } = require('node:events');

// Keep updater policy independent of Electron so shutdown and failure paths can be tested.
class Updates extends EventEmitter {
  constructor({ updater, enabled, notify, log = console }) {
    super();
    this.updater = updater;
    this.enabled = enabled;
    this.notify = notify;
    this.state = enabled ? 'idle' : 'disabled';
    this.version = null;
    this.installing = false;
    updater.autoDownload = true;
    // app.exit() is used by the service lifecycle. Installation must only happen
    // through our explicit restart action after both owned services have stopped.
    updater.autoInstallOnAppQuit = false;
    updater.allowPrerelease = false;
    updater.allowDowngrade = false;
    updater.logger = log;
    updater.on('checking-for-update', () => this.setState('checking'));
    updater.on('update-available', info => { this.version = info.version; this.setState('downloading'); });
    updater.on('download-progress', progress => this.setState('downloading', Math.floor(progress.percent)));
    updater.on('update-not-available', () => this.setState('idle'));
    updater.on('update-downloaded', info => {
      this.version = info.version;
      this.setState('downloaded');
      notify(`DevFlow ${info.version} 已下載`, '可從 DevFlow 選單或系統匣選擇「重新啟動以更新」。任務會繼續執行。');
    });
    updater.on('error', error => {
      log.warn('Desktop update failed', error);
      // A ready installer remains usable if an unrelated later check fails.
      if (this.state !== 'downloaded') this.setState('error');
      this.emit('update-error', error);
    });
  }
  setState(state, percent) {
    this.state = state;
    this.percent = percent;
    this.emit('changed');
  }
  get label() {
    if (!this.enabled) return '檢查更新（僅安裝版提供）';
    if (this.state === 'downloaded') return `重新啟動以更新至 ${this.version}`;
    if (this.state === 'checking') return '正在檢查更新…';
    if (this.state === 'downloading') return `正在下載更新${this.percent == null ? '…' : ` ${this.percent}%`}`;
    return this.state === 'error' ? '更新失敗，重新檢查…' : '檢查更新';
  }
  get actionable() {
    return this.enabled && !this.installing && !['checking', 'downloading'].includes(this.state);
  }
  async check(manual = false) {
    if (!this.actionable || this.state === 'downloaded') return;
    this.setState('checking');
    try {
      const result = await this.updater.checkForUpdates();
      if (manual && this.state === 'idle') this.notify('DevFlow 已是最新版本', '目前沒有較新的穩定版本。');
      // Download failures also reject this promise; always observe it.
      if (result?.downloadPromise) await result.downloadPromise;
    } catch (error) {
      if (this.state !== 'downloaded') this.setState('error');
      if (manual) this.notify('無法取得 DevFlow 更新', '請檢查網路或 Releases 發布設定，再從選單重試。');
    }
  }
  async install({ confirm, stop, recover }) {
    if (!this.enabled || this.state !== 'downloaded' || this.installing) return;
    this.installing = true;
    this.emit('changed');
    let handedOff = false;
    try {
      if (!await confirm()) return;
      await stop();
      this.updater.quitAndInstall(true, true);
      handedOff = true;
    } catch (error) {
      await recover(error);
    } finally {
      if (!handedOff) this.installing = false;
      this.emit('changed');
    }
  }
}

function hasActiveWork(tasks, discussions) {
  return tasks.some(task => ['Running', 'Planning', 'Merging', 'Queued', 'PlanningQueued'].includes(task.status)) ||
    discussions.some(discussion => discussion.activeOperation != null);
}

module.exports = { Updates, hasActiveWork };
