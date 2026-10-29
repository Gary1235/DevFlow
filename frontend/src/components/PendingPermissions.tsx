import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api';
import { useResource } from '../hooks';

export interface PendingPermission {
  id: string;
  ownerId: string;
  kind: 'task' | 'discussion';
  title: string;
  tool: string;
  provider: string;
  createdAt: string;
  reminderAt: string;
  reasonCode: string;
  reason: string;
}

export function PendingPermissions() {
  const resource = useResource(
    'pending-permissions',
    useCallback(
      (signal: AbortSignal) =>
        api<PendingPermission[]>('/api/permissions/pending', 'GET', undefined, { signal }),
      []
    ),
    5000,
    true,
    true
  );

  const [notifications, setNotifications] = useState(
    () => localStorage.getItem('devflow-permission-notifications') === 'true'
  );
  const [sound, setSound] = useState(
    () => localStorage.getItem('devflow-permission-sound') === 'true'
  );
  const [message, setMessage] = useState('');
  const [isDropdownOpen, setIsDropdownOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const seenPermissionsRef = useRef(new Set<string>());
  const pending = resource.data || [];
  const getPermissionHref = (permissionItem: PendingPermission) =>
    '#' + permissionItem.kind + '/' + permissionItem.ownerId;

  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        setIsDropdownOpen(false);
      }
    }
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  useEffect(() => {
    for (const permissionItem of pending) {
      if (seenPermissionsRef.current.has(permissionItem.id)) continue;
      seenPermissionsRef.current.add(permissionItem.id);

      if (
        notifications &&
        typeof Notification !== 'undefined' &&
        Notification.permission === 'granted'
      ) {
        try {
          const notificationInstance = new Notification('DevFlow 等待你的核准', {
            body: permissionItem.title + ' · ' + permissionItem.reason,
            tag: 'devflow-' + permissionItem.id,
          });
          notificationInstance.onclick = () => {
            window.focus();
            location.hash = getPermissionHref(permissionItem);
            notificationInstance.close();
          };
        } catch {
          setMessage('系統無法顯示通知，請查看全域待確認清單。');
        }
      }

      if (sound) {
        try {
          const audioContext = new AudioContext();
          const oscillator = audioContext.createOscillator();
          const gainNode = audioContext.createGain();
          gainNode.gain.value = 0.08;
          oscillator.connect(gainNode);
          gainNode.connect(audioContext.destination);
          oscillator.start();
          oscillator.stop(audioContext.currentTime + 0.2);
          oscillator.onended = () => {
            void audioContext.close();
          };
        } catch {
          /* Sound is optional and browsers may require a user gesture. */
        }
      }
    }

    const activeIds = new Set(pending.map(permissionItem => permissionItem.id));
    for (const seenId of seenPermissionsRef.current) {
      if (!activeIds.has(seenId)) seenPermissionsRef.current.delete(seenId);
    }
  }, [resource.data, notifications, sound]);

  useEffect(() => {
    if (!pending.length) return;
    const titlePrefix = `[待核准 ${pending.length}] `;
    const updateDocumentTitle = () => {
      if (!document.title.startsWith(titlePrefix)) {
        document.title = titlePrefix + document.title.replace(/^\[待核准 \d+\] /, '');
      }
    };
    updateDocumentTitle();
    const mutationObserver = new MutationObserver(updateDocumentTitle);
    const titleElement = document.querySelector('title');
    if (titleElement) {
      mutationObserver.observe(titleElement, {
        childList: true,
        subtree: true,
        characterData: true,
      });
    }
    return () => {
      mutationObserver.disconnect();
      document.title = document.title.replace(/^\[待核准 \d+\] /, '');
    };
  }, [pending.length]);

  async function enableNotifications() {
    if (typeof Notification === 'undefined') {
      setMessage('此瀏覽器不支援桌面通知，請查看待確認清單。');
      return;
    }
    const permissionResult = await Notification.requestPermission();
    const isGranted = permissionResult === 'granted';
    setNotifications(isGranted);
    localStorage.setItem('devflow-permission-notifications', String(isGranted));
    seenPermissionsRef.current.clear();
    setMessage(
      isGranted ? '已啟用工具確認通知。' : '瀏覽器未允許通知，請在網站設定開啟。'
    );
  }

  const isDropdownVisible = isDropdownOpen || pending.length > 0;

  return (
    <div
      ref={containerRef}
      className="pending-permissions-container"
      aria-label="全域工具確認"
    >
      <button
        type="button"
        className={
          'pending-permissions-trigger' +
          (pending.length > 0 ? ' has-pending' : '') +
          (isDropdownOpen ? ' active' : '')
        }
        aria-expanded={isDropdownOpen}
        aria-label={'工具待確認 ' + pending.length}
        onClick={() => setIsDropdownOpen(currentValue => !currentValue)}
      >
        <span className="pending-trigger-icon" aria-hidden="true">
          🔔
        </span>
        <span className="pending-trigger-count">待確認 {pending.length}</span>
        {pending.length > 0 && <span className="pending-badge-dot" />}
      </button>

      <div
        className={
          'pending-permissions-popover' + (isDropdownVisible ? ' open' : ' closed')
        }
      >
        <div className="pending-popover-header">
          <div className="button-row">
            <strong>工具待確認 {pending.length}</strong>
            <button
              type="button"
              className="secondary"
              onClick={() => {
                if (notifications) {
                  setNotifications(false);
                  localStorage.setItem('devflow-permission-notifications', 'false');
                } else {
                  void enableNotifications();
                }
              }}
            >
              {notifications ? '關閉核准通知' : '啟用核准通知'}
            </button>
            <label className="pending-sound-toggle">
              <input
                type="checkbox"
                checked={sound}
                onChange={event => {
                  setSound(event.target.checked);
                  localStorage.setItem(
                    'devflow-permission-sound',
                    String(event.target.checked)
                  );
                }}
              />
              提示音
            </label>
          </div>
        </div>

        {message && <p role="status" className="pending-status-msg">{message}</p>}
        {resource.error && (
          <p role="alert" className="error-banner">
            無法取得待確認清單：{resource.error}
            <button type="button" className="secondary" onClick={resource.refresh}>
              重試
            </button>
          </p>
        )}

        {pending.length > 0 && (
          <div className="info-banner" role="status">
            任務正在等待你的工具核准。
            <ul>
              {pending.map(permissionItem => (
                <li key={permissionItem.id}>
                  <a href={getPermissionHref(permissionItem)}>{permissionItem.title}</a> ·{' '}
                  {permissionItem.provider}／{permissionItem.tool} · 已等待{' '}
                  {Math.max(
                    0,
                    Math.floor(
                      (Date.now() - Date.parse(permissionItem.createdAt)) / 60000
                    )
                  )}{' '}
                  分鐘
                  <p>
                    {permissionItem.reason}（{permissionItem.reasonCode}）
                  </p>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </div>
  );
}
