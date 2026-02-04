# FilesController API Endpoints

## Overview
The `FilesController` provides endpoints for file and folder management, including upload, download, list, delete, rename, folder creation, and video thumbnail generation. All endpoints use async/await and the Result<T> pattern for responses.

## Endpoints

### POST `/api/files/upload`
- **Description:** Upload a file to a folder (admin only)
- **Body:** multipart/form-data (file, folderId)
- **Returns:** `Result<FileMetadata>`

### GET `/api/files/download/{id}`
- **Description:** Download a file by ID
- **Returns:** File stream

### GET `/api/files/list?folderId={folderId}`
- **Description:** List files in a folder (all users)
- **Returns:** `Result<List<FileMetadata>>`

### DELETE `/api/files/{id}`
- **Description:** Delete a file by ID (admin only)
- **Returns:** `Result<bool>`

### PUT `/api/files/rename/{id}`
- **Description:** Rename a file (admin only)
- **Body:** string (new name)
- **Returns:** `Result<FileMetadata>`

### POST `/api/files/folders`
- **Description:** Create a folder (admin only)
- **Body:** string (name), query: parentFolderId
- **Returns:** `Result<Folder>`

### PUT `/api/files/folders/rename/{id}`
- **Description:** Rename a folder (admin only)
- **Body:** string (new name)
- **Returns:** `Result<Folder>`

### DELETE `/api/files/folders/{id}`
- **Description:** Delete a folder (admin only)
- **Returns:** `Result<bool>`

### GET `/api/files/folders/list?parentFolderId={parentFolderId}`
- **Description:** List folders (all users)
- **Returns:** `Result<List<Folder>>`

### POST `/api/files/thumbnail/{id}`
- **Description:** Generate a video thumbnail for a file (admin only)
- **Returns:** `Result<FileMetadata>`

## Notes
- Only admins can upload, delete, or rename files/folders.
- All users can view and download files.
- Thumbnails are generated using FFmpeg and stored as JPEGs.
