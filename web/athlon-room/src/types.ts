export interface RoomAttachment {
  id: string;
  fileName: string;
  mimeType: string;
  size: number;
  isImage: boolean;
  storedName?: string;
  previewUrl?: string;
}

export interface RoomMessage {
  id: string;
  senderId: string;
  displayName: string;
  kind: "text" | "image" | "file" | string;
  text?: string | null;
  attachments: RoomAttachment[];
  createdAt: string;
}

export interface RoomMeta {
  id: string;
  title: string;
  createdAt: string;
}

export interface PendingFile {
  id: string;
  file: File;
  previewUrl?: string;
}
