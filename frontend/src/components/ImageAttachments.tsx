import { useRef, useState } from 'react';
import type { ClipboardEvent, DragEvent } from 'react';
import { Modal, useNotice } from './ui';
import type { DiscussionImage, ImageUpload, ProviderId } from '../types';

const maxBytes = 4 * 1024 * 1024;
const supportedMimeTypes = ['image/png', 'image/jpeg', 'image/webp'];

export function useImageAttachments() {
  const [images, setImages] = useState<ImageUpload[]>([]);
  const [reading, setReading] = useState(false);
  const currentImagesRef = useRef<ImageUpload[]>([]);
  const queueRef = useRef(Promise.resolve());
  const notice = useNotice();
  const pendingCountRef = useRef(0);

  function updateImages(nextImages: ImageUpload[]) {
    currentImagesRef.current = nextImages;
    setImages(nextImages);
  }

  function addFiles(files: File[]) {
    if (!files.length) return;
    setReading(true);
    pendingCountRef.current++;
    queueRef.current = queueRef.current
      .then(async () => {
        for (const fileItem of files) {
          if (currentImagesRef.current.length >= 4) {
            notice('每則訊息最多附上 4 張圖片。', true);
            break;
          }
          if (
            !supportedMimeTypes.includes(fileItem.type) ||
            !fileItem.size ||
            fileItem.size > maxBytes
          ) {
            notice('請使用 PNG、JPEG 或 WebP，每張上限 4 MB。', true);
            continue;
          }
          try {
            const base64Data = await new Promise<string>((resolve, reject) => {
              const fileReader = new FileReader();
              fileReader.onload = () => resolve(String(fileReader.result).split(',')[1]);
              fileReader.onerror = () => reject(new Error('無法讀取圖片'));
              fileReader.readAsDataURL(fileItem);
            });
            updateImages([
              ...currentImagesRef.current,
              {
                name: (fileItem.name || '貼上圖片.png').slice(0, 200),
                mimeType: fileItem.type,
                data: base64Data,
              },
            ]);
          } catch {
            notice('無法讀取圖片，請重新附上。', true);
          }
        }
      })
      .finally(() => {
        if (--pendingCountRef.current === 0) setReading(false);
      });
  }

  function handlePaste(event: ClipboardEvent) {
    const files = Array.from(event.clipboardData.items || [])
      .filter(item => item.kind === 'file')
      .map(item => item.getAsFile())
      .filter((fileItem): fileItem is File => Boolean(fileItem));
    if (files.length) {
      event.preventDefault();
      addFiles(files);
    }
  }

  function handleDrop(event: DragEvent) {
    if (event.dataTransfer.files.length) {
      event.preventDefault();
      addFiles(Array.from(event.dataTransfer.files));
    }
  }

  return {
    images,
    reading,
    add: addFiles,
    paste: handlePaste,
    drop: handleDrop,
    clear: () => updateImages([]),
    remove: (targetIndex: number) =>
      updateImages(
        currentImagesRef.current.filter((_, itemIndex) => itemIndex !== targetIndex)
      ),
  };
}

export type DraftAttachments = ReturnType<typeof useImageAttachments>;

export function ImageThumbnailStrip({
  draft,
  busy,
  provider,
  hasHistory = false,
}: {
  draft: DraftAttachments;
  busy: boolean;
  provider: ProviderId;
  hasHistory?: boolean;
}) {
  const [previewImage, setPreviewImage] = useState<ImageUpload | null>(null);

  if (
    draft.images.length === 0 &&
    !draft.reading &&
    !(hasHistory && provider === 'antigravity')
  ) {
    return null;
  }

  return (
    <div className="chat-composer-attachments-top">
      {draft.reading && (
        <span className="chat-reading-indicator" role="status">
          讀取圖片中…
        </span>
      )}
      {(draft.images.length > 0 || hasHistory) && provider === 'antigravity' && (
        <p role="status" className="error-banner">
          Antigravity 背景模式目前只接受文字。圖片已保留，請切換 Codex、Claude 或支援圖片的 Gemini CLI。
        </p>
      )}
      {draft.images.length > 0 && (
        <div className="chat-image-thumbnails">
          {draft.images.map((imageItem, imageIndex) => (
            <div className="chat-image-thumbnail-card" key={imageIndex}>
              <button
                type="button"
                className="chat-image-preview"
                aria-label={'預覽圖片 ' + imageItem.name}
                onClick={() => setPreviewImage(imageItem)}
              >
                <img
                  src={'data:' + imageItem.mimeType + ';base64,' + imageItem.data}
                  alt={imageItem.name}
                />
              </button>
              <span className="chat-image-name" title={imageItem.name}>
                {imageItem.name}
              </span>
              <button
                type="button"
                className="chat-image-remove"
                disabled={busy || draft.reading}
                aria-label={'移除圖片 ' + imageItem.name}
                onClick={() => draft.remove(imageIndex)}
              >
                ✕
              </button>
            </div>
          ))}
        </div>
      )}
      {previewImage && (
        <Modal title={previewImage.name} onClose={() => setPreviewImage(null)} wide>
          <div className="dialog-body image-full-preview">
            <img
              src={'data:' + previewImage.mimeType + ';base64,' + previewImage.data}
              alt={previewImage.name}
            />
          </div>
        </Modal>
      )}
    </div>
  );
}

export function ImageUploadButton({
  draft,
  busy,
}: {
  draft: DraftAttachments;
  busy: boolean;
}) {
  const fileInputRef = useRef<HTMLInputElement>(null);

  return (
    <div className="chat-image-uploader">
      <input
        ref={fileInputRef}
        type="file"
        hidden
        multiple
        accept="image/png,image/jpeg,image/webp"
        aria-label="選擇圖片"
        disabled={busy || draft.reading}
        onChange={event => {
          draft.add(Array.from(event.target.files || []));
          event.target.value = '';
        }}
      />
      <button
        type="button"
        className="chat-attach-button"
        aria-label="附加圖片"
        title="附加圖片"
        disabled={busy || draft.reading || draft.images.length >= 4}
        onClick={() => fileInputRef.current?.click()}
      >
        ＋
      </button>
    </div>
  );
}

export function ImageAttachments({
  draft,
  busy,
  provider,
  hasHistory = false,
}: {
  draft: DraftAttachments;
  busy: boolean;
  provider: ProviderId;
  hasHistory?: boolean;
}) {
  return (
    <>
      <ImageThumbnailStrip
        draft={draft}
        busy={busy}
        provider={provider}
        hasHistory={hasHistory}
      />
      <ImageUploadButton draft={draft} busy={busy} />
    </>
  );
}

export function MessageImages({
  discussionId,
  images,
}: {
  discussionId: string;
  images?: DiscussionImage[] | null;
}) {
  const [previewImage, setPreviewImage] = useState<DiscussionImage | null>(null);
  const getImageUrl = (imageItem: DiscussionImage) =>
    '/api/discussions/' + discussionId + '/images/' + imageItem.id;

  return (
    <>
      <div className="chat-image-list">
        {images?.map(imageItem => (
          <button
            type="button"
            className="chat-image-preview"
            key={imageItem.id}
            onClick={() => setPreviewImage(imageItem)}
            aria-label={'檢視圖片 ' + imageItem.name}
          >
            <img src={getImageUrl(imageItem)} alt={imageItem.name} loading="lazy" />
          </button>
        ))}
      </div>
      {previewImage && (
        <Modal title={previewImage.name} onClose={() => setPreviewImage(null)} wide>
          <div className="dialog-body image-full-preview">
            <img src={getImageUrl(previewImage)} alt={previewImage.name} />
          </div>
        </Modal>
      )}
    </>
  );
}
