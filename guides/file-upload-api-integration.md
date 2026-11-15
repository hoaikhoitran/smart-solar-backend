# Upload ảnh lên AWS S3 — Hướng dẫn tích hợp Frontend

Bucket S3 là **private**. Frontend không bao giờ có AWS key. Backend cấp một URL `PUT` đã ký, sống ngắn hạn,
cho đúng một object key do backend tạo. Sau khi upload, frontend đăng ký key đó để backend **xác minh** file
(tồn tại, đúng dung lượng, đúng loại theo magic bytes, SHA-256, S3 version). Chỉ file `VERIFIED` mới được gắn vào nghiệp vụ.

Envelope giữ nguyên: `{ "isSuccess", "traceId", "data", "error": { "code", "message", "details" } }`.

## 1. Tổng quan 4 bước

```
FE ──(1) POST /api/files/presigned-upload ──────────────► API      → uploadUrl, objectKey, headers
FE ──(2) PUT bytes  →  uploadUrl ───────────────────────► S3       (không gửi Bearer token)
FE ──(3) POST /api/files {objectKey,…} ─────────────────► API      → fileId, status VERIFIED / PENDING / REJECTED
FE ──(4) POST …/attachments | …/evidence {fileId} ──────► API      (chỉ nhận file VERIFIED của chính người gọi)
```

## 2. Ai được upload purpose nào

| Purpose | Role được xin URL | Định dạng | Tối đa | Key prefix (backend tự tạo) |
|---|---|---|---|---|
| `ONSITE_SURVEY_EVIDENCE` | Technician | JPEG, PNG, WebP, PDF | 15 MB | `onsite-surveys/{userId}/` |
| `INSTALLATION_EVIDENCE` | Technician | JPEG, PNG, WebP | 15 MB | `installations/{userId}/` |
| `SERVICE_EVIDENCE` | Technician, Customer | JPEG, PNG, WebP | 15 MB | `after-sales/{userId}/` |
| `QUOTATION_DOCUMENT` | Sales | PDF | 20 MB | `quotations/{userId}/` |
| `CONTRACT_DOCUMENT` | Sales | PDF | 20 MB | `contracts/official/{userId}/` |
| `SIGNED_CONTRACT` | Customer | PDF | 20 MB | `contracts/signed/{userId}/` |
| `PROJECT_DOCUMENT` | Manager, Admin | PDF, JPEG, PNG | 20 MB | `project-documents/{userId}/` |
| `KNOWLEDGE_DOCUMENT` | Admin | PDF, DOCX, TXT, MD | 50 MB | `knowledge/{userId}/` |
| `PRODUCT_IMAGE` (ảnh sản phẩm, công khai qua mục 7b) | Admin, Manager | JPEG, PNG, WebP | 10 MB | `products/{userId}/` |
| `PACKAGE_IMAGE` (ảnh bìa gói dịch vụ, công khai qua mục 7b) | Admin | JPEG, PNG, WebP | 10 MB | `packages/{userId}/` |

* `fileName` phải có đuôi khớp `contentType` (`.jpg`/`.jpeg` cho `image/jpeg`, `.png`, `.webp`, `.pdf`).
* **HEIC/HEIF (ảnh iPhone) không được hỗ trợ.** Hãy chuyển sang JPEG trước khi upload (ví dụ vẽ ảnh lên `<canvas>` rồi
  `canvas.toBlob(cb, "image/jpeg", 0.9)`, hoặc dùng thư viện như `heic2any`). Nhiều trình duyệt đã tự chuyển khi chọn ảnh
  qua `<input type="file" accept="image/jpeg,image/png,image/webp">`.

## 3. Bước 1 — Xin URL upload

`POST /api/files/presigned-upload` (Bearer JWT)

```json
{ "purpose": "ONSITE_SURVEY_EVIDENCE", "fileName": "roof.jpg", "contentType": "image/jpeg", "sizeBytes": 2048000 }
```

200:

```json
{
  "isSuccess": true,
  "traceId": "…",
  "data": {
    "uploadUrl": "https://<bucket>.s3.ap-southeast-1.amazonaws.com/onsite-surveys/<userId>/<random>.jpg?X-Amz-Algorithm=…&X-Amz-Signature=…",
    "method": "PUT",
    "bucket": "<bucket>",
    "objectKey": "onsite-surveys/<userId>/<random>.jpg",
    "headers": { "Content-Type": "image/jpeg" },
    "expiresAt": "2026-10-21T12:05:00+00:00"
  },
  "error": null
}
```

* `sizeBytes` là dung lượng thật của file (`file.size`), dùng lại ở bước 3.
* URL hết hạn sau `Storage__S3__PresignedUrlTtlSeconds` (mặc định 300 giây). Hết hạn thì xin URL mới, đừng lưu URL lại.
* Mỗi lần gọi trả một `objectKey` mới. Không tự tạo hay sửa `objectKey`.

## 4. Bước 2 — PUT file lên S3

* Gửi **bytes của file** (đối tượng `File`/`Blob`), không bọc JSON, không dùng `FormData`.
* Gửi **đúng** các header trong `headers` (Content-Type nằm trong chữ ký; sai một ký tự → S3 trả 403 `SignatureDoesNotMatch`).
* **Không** gửi `Authorization: Bearer …` của backend tới S3.
* S3 trả 200 (body rỗng) là upload xong.

## 5. Bước 3 — Đăng ký file

`POST /api/files`

```json
{ "purpose": "ONSITE_SURVEY_EVIDENCE", "objectKey": "onsite-surveys/<userId>/<random>.jpg", "fileName": "roof.jpg", "contentType": "image/jpeg", "sizeBytes": 2048000 }
```

201 trả về `data.fileId` và `data.status`. **201 không có nghĩa là dùng được** — luôn kiểm tra `status`:

| `status` | Ý nghĩa | FE làm gì |
|---|---|---|
| `VERIFIED` | Đã xác minh với S3 | Dùng `fileId` ở bước 4 |
| `PENDING_VERIFICATION` | Tạm thời chưa kiểm tra được S3 | Gọi lại `POST /api/files/{fileId}/verify` sau vài giây (tối đa vài lần) |
| `REJECTED` | Không hợp lệ | Hiển thị `data.rejectionReason` (vd. "The object does not exist in storage.", "The stored object size does not match the declared size.", "The stored content does not match an allowed file type."), upload lại từ bước 1 |

`GET /api/files/{fileId}` xem lại trạng thái (chỉ người upload hoặc staff).

## 6. Bước 4 — Gắn ảnh vào nghiệp vụ

| Luồng | Endpoint | Body |
|---|---|---|
| Khảo sát (Technician) | `POST /api/technicians/me/appointments/{appointmentId}/onsite-survey/attachments` | `{ "fileId", "category": "ROOF" \| "ELECTRICAL" \| "OBSTACLE" \| "SITE" \| "DOCUMENT", "caption"? }` |
| Thi công (Technician) | `POST /api/technicians/me/installation-projects/{projectId}/evidence` | `{ "fileId", "caption"? }` |
| Bảo hành / sửa chữa (Technician) | `POST /api/technicians/me/after-sales/visits/{visitId}/evidence` | `{ "fileId", "caption"? }` |
| Báo lỗi (Customer) | `POST /api/customers/me/after-sales/fault-requests` | `{ …, "evidenceFileIds": [fileId, …] }` (tối đa 10) |
| Phản hồi nghiệm thu / khiếu nại (Customer) | `POST /api/customers/me/after-sales/requests/{requestId}/completion` hoặc `/reconsideration` | `{ …, "evidenceFileIds": [ … ] }` |

Các API này vẫn kiểm tra: đúng người, đúng nghiệp vụ, đúng trạng thái, đúng purpose, file `VERIFIED` và do chính người gọi upload.

## 7. Hiển thị lại ảnh

Không có URL S3 public. Xin một **presigned GET URL** cho từng ảnh mỗi lần hiển thị (`?disposition=inline` mặc định,
hoặc `attachment` để tải về). Response: `{ id, url, expiresAt, disposition, contentType, fileName }` (Installation trả
`evidenceId` thay cho `id`). Dùng `url` trong `<img src>`; không lưu sau `expiresAt`.

| Luồng | Lấy danh sách (id) | Lấy URL |
|---|---|---|
| Khảo sát — Technician | `GET /api/technicians/me/appointments/{appointmentId}/onsite-survey` → `attachments[].attachmentId` | `GET …/onsite-survey/attachments/{attachmentId}/url` |
| Khảo sát — Sales chủ request, Manager, Admin | `GET /api/survey-requests/{surveyRequestId}/onsite-survey` | `GET /api/survey-requests/{surveyRequestId}/onsite-survey/attachments/{attachmentId}/url` |
| Thi công | `GET …/installation-projects/{projectId}/evidence` (customer, technician, staff — giữ nguyên) | `GET …/evidence/{evidenceId}/url` |
| Bảo hành — Customer | `GET /api/customers/me/after-sales/requests/{requestId}` → `evidence[].evidenceId` | `GET /api/customers/me/after-sales/requests/{requestId}/evidence/{evidenceId}/url` |
| Bảo hành — Technician | `GET /api/technicians/me/after-sales/visits/{visitId}` → `evidence[]` (mới) | `GET /api/technicians/me/after-sales/visits/{visitId}/evidence/{evidenceId}/url` |
| Bảo hành — Manager, Admin | `GET /api/manager/after-sales/requests/{requestId}` | `GET /api/manager/after-sales/requests/{requestId}/evidence/{evidenceId}/url` |

Người không có quyền nhận 404 (không lộ thông tin).

## 7b. Ảnh công khai của sản phẩm và gói dịch vụ (14/11/2026)

Ảnh sản phẩm (`ProductImage.url`) và ảnh bìa gói dịch vụ (`ServicePackage.imageUrl`) là **URL cố định** mà khách chưa đăng
nhập cũng xem được, nên không dùng presigned GET ngắn hạn. Bucket vẫn private; API đứng giữa:

```
Admin ──(1..3) presigned-upload / PUT / POST /api/files  purpose PRODUCT_IMAGE | PACKAGE_IMAGE ──► fileId (VERIFIED)
Admin ──(4) POST /api/admin/products/{id}/images { "fileId", "altText"?, "makePrimary"? }
        hoặc POST/PUT /api/admin/after-sales/packages { …, "imageFileId" }
API lưu url = {Storage:PublicFileBaseUrl}/api/files/public/{fileId}
Mọi người ──GET /api/files/public/{fileId} (không token) ──► 302 tới presigned GET của object (Cache-Control: public, max-age ≤ 240)
```

* `fileId` chỉ được nhận khi là tệp `VERIFIED` đúng purpose của chính người gọi; gửi cả `url` (hoặc `imageUrl`) và `fileId`
  (`imageFileId`) trong một request → 400 `AUTH_VALIDATION_FAILED` / `AFTER_SALES_VALIDATION_FAILED`. API cũ với link https
  ngoài hệ thống giữ nguyên.
* `GET /api/files/public/{fileId}` là **anonymous**, giới hạn 120 lần / phút / IP (`FilesPublicReadPolicy`); chỉ trả
  302 cho tệp `VERIFIED` của hai purpose trên, mọi trường hợp khác 404 (không lộ tệp nghiệp vụ). Trình duyệt đi theo 302 và
  cache theo `max-age` (bằng TTL của link ký trừ 60 giây, tối đa 240 giây), nên `<img src>` dùng thẳng URL đã lưu.
* **Cấu hình bắt buộc**: `Storage__PublicFileBaseUrl` = origin https công khai của API (ví dụ `https://api.example.com`,
  không query / fragment). Chưa cấu hình thì upload từ máy vẫn đăng ký tệp được nhưng bước 4 trả 503
  `STORAGE_PUBLIC_URL_NOT_CONFIGURED`; ảnh bằng link https ngoài không bị ảnh hưởng.
* IAM: thêm `products/*` và `packages/*` vào policy ở mục 10. CORS bucket không đổi (PUT từ FE); GET ảnh đi qua 302 tới
  presigned URL nên không cần CORS cho `<img>` (chỉ cần nếu FE đọc ảnh bằng `fetch`/canvas).

## 8. Ví dụ TypeScript (`fetch`)

```ts
const API = "https://api.example.com";   // backend base URL

type Envelope<T> = { isSuccess: boolean; data: T | null; error: { code: string; message: string } | null };

async function api<T>(path: string, token: string, body?: unknown): Promise<T> {
  const res = await fetch(`${API}${path}`, {
    method: body === undefined ? "GET" : "POST",
    headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const json = (await res.json()) as Envelope<T>;
  if (!json.isSuccess) throw new Error(`${json.error?.code}: ${json.error?.message}`);
  return json.data as T;
}

/** Uploads one file and returns the fileId of the VERIFIED file. */
export async function uploadFile(file: File, purpose: string, token: string): Promise<string> {
  // 1. Upload URL
  const upload = await api<{ uploadUrl: string; method: string; objectKey: string; headers: Record<string, string> }>(
    "/api/files/presigned-upload", token, { purpose, fileName: file.name, contentType: file.type, sizeBytes: file.size });

  // 2. PUT the bytes to S3: exactly the returned headers, no Authorization header.
  const put = await fetch(upload.uploadUrl, { method: upload.method, headers: upload.headers, body: file });
  if (!put.ok) throw new Error(`S3 upload failed (${put.status})`);

  // 3. Register, then check the status (201 does not mean VERIFIED).
  let registered = await api<{ fileId: string; status: string; rejectionReason: string | null }>("/api/files", token,
    { purpose, objectKey: upload.objectKey, fileName: file.name, contentType: file.type, sizeBytes: file.size });
  for (let attempt = 0; registered.status === "PENDING_VERIFICATION" && attempt < 3; attempt++) {
    await new Promise((r) => setTimeout(r, 2000));
    registered = await api(`/api/files/${registered.fileId}/verify`, token, {});
  }
  if (registered.status !== "VERIFIED") throw new Error(registered.rejectionReason ?? `File is ${registered.status}`);
  return registered.fileId;
}

// 4. Attach, e.g. an on-site survey roof photo:
// const fileId = await uploadFile(file, "ONSITE_SURVEY_EVIDENCE", token);
// await api(`/api/technicians/me/appointments/${appointmentId}/onsite-survey/attachments`, token, { fileId, category: "ROOF", caption: "Mái phía nam" });

// Display: ask for a fresh URL each time.
// const { url } = await api<{ url: string }>(`/api/technicians/me/appointments/${appointmentId}/onsite-survey/attachments/${attachmentId}/url`, token);
// img.src = url;
```

`file.type` có thể rỗng trên một số thiết bị; khi đó suy ra từ đuôi file (`.jpg` → `image/jpeg`) trước khi gọi bước 1.

## 9. Lỗi thường gặp

| HTTP | `error.code` | Nguyên nhân / cách xử lý |
|---|---|---|
| 401 | `AUTH_UNAUTHORIZED` | Thiếu hoặc hết hạn JWT |
| 403 | `FILE_UPLOAD_NOT_ALLOWED` | Role không được upload purpose này |
| 400 | `FILE_INVALID_PURPOSE` | `purpose` không tồn tại |
| 400 | `FILE_TYPE_NOT_ALLOWED` | Loại file / đuôi không hợp lệ (vd. HEIC) |
| 400 | `FILE_TOO_LARGE` | `sizeBytes` ≤ 0 hoặc vượt giới hạn |
| 400 | `FILE_INVALID_OBJECT_KEY` | `objectKey` gửi ở bước 3 không phải key backend cấp cho chính bạn |
| 409 | `FILE_ALREADY_REGISTERED` | Key đã đăng ký; upload lại từ bước 1 |
| 409 | `FILE_NOT_VERIFIED` | Gắn file chưa `VERIFIED` |
| 403 | `FILE_NOT_OWNED` | Gắn file do người khác upload |
| 400 | `FILE_WRONG_PURPOSE` | Gắn file của purpose khác |
| 503 | `STORAGE_NOT_CONFIGURED` | Môi trường chưa bật S3 |
| 503 | `STORAGE_UNAVAILABLE` | Không tạo được URL / không truy cập được S3; thử lại sau |
| 503 | `STORAGE_PUBLIC_URL_NOT_CONFIGURED` | Gắn `fileId` / `imageFileId` cho ảnh sản phẩm / gói khi `Storage__PublicFileBaseUrl` chưa cấu hình (mục 7b) |
| 410 | `FILE_OBJECT_MISSING`, `INSTALLATION_EVIDENCE_OBJECT_MISSING` | Object đã bị xóa khỏi S3 |
| 404 | `ONSITE_SURVEY_ATTACHMENT_NOT_FOUND`, `SERVICE_EVIDENCE_NOT_FOUND` | Sai id hoặc không có quyền |
| S3 403 | `SignatureDoesNotMatch` | Header khác với `headers` được cấp, hoặc thêm header lạ |
| S3 403 | `AccessDenied` / `Request has expired` | URL hết hạn, hoặc credential backend thiếu `s3:PutObject` |
| (trình duyệt) | CORS error ở bước 2 | Bucket chưa có CORS cho origin FE (mục 10) |
| (trình duyệt) | `status 0` / "Network Error" ở bước 2, mạng vẫn ổn | Trình duyệt chặn vì response không có header CORS: bucket chưa có CORS cho origin FE, **hoặc** `Storage__S3__Region` khác vùng thật của bucket (S3 trả 301 không kèm CORS), **hoặc** bucket không tồn tại. FE báo "Không tải được tệp lên. Mạng vẫn ổn thì báo quản trị hệ thống." Chẩn đoán ở mục 10b |

## 10. Cấu hình AWS (thực hiện thủ công)

Có **hai** cấu hình CORS khác nhau:

1. **CORS của API** (`Cors__AllowedOrigins__0=https://fe.example.com`…): cho phép FE gọi backend (bước 1, 3, 4, xem ảnh). Đã có sẵn.
2. **CORS của bucket S3**: cho phép trình duyệt `PUT` trực tiếp lên S3 (bước 2) và `GET` ảnh. Cấu hình trong S3 Console →
   bucket → Permissions → Cross-origin resource sharing (CORS):

```json
[
  {
    "AllowedOrigins": ["http://localhost:3000", "https://your-frontend-domain.example"],
    "AllowedMethods": ["PUT", "GET", "HEAD"],
    "AllowedHeaders": ["Content-Type"],
    "ExposeHeaders": ["ETag", "x-amz-version-id"],
    "MaxAgeSeconds": 3000
  }
]
```

Bucket:
* Giữ **Block Public Access** bật toàn bộ; không dùng ACL public-read, không cần bucket policy public.
* Bật **Versioning** (bắt buộc khi `Storage__S3__RequireObjectVersioning=true`).

Biến môi trường backend: `Storage__S3__Enabled=true`, `Storage__DefaultBucket`, `Storage__AllowedBuckets`
(tùy chọn), `Storage__S3__Region`, credential qua IAM role (khuyến nghị) hoặc `Storage__S3__AccessKeyId` /
`Storage__S3__SecretAccessKey`, `Storage__S3__PresignedUrlTtlSeconds`; mới từ 14/11/2026: `Storage__PublicFileBaseUrl` (mục 7b).

IAM policy tối thiểu cho credential của backend (thay `YOUR_BUCKET`):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "UploadAndVerifyAppFiles",
      "Effect": "Allow",
      "Action": ["s3:PutObject", "s3:GetObject", "s3:GetObjectVersion"],
      "Resource": [
        "arn:aws:s3:::YOUR_BUCKET/onsite-surveys/*",
        "arn:aws:s3:::YOUR_BUCKET/installations/*",
        "arn:aws:s3:::YOUR_BUCKET/after-sales/*",
        "arn:aws:s3:::YOUR_BUCKET/quotations/*",
        "arn:aws:s3:::YOUR_BUCKET/contracts/*",
        "arn:aws:s3:::YOUR_BUCKET/project-documents/*",
        "arn:aws:s3:::YOUR_BUCKET/knowledge/*",
        "arn:aws:s3:::YOUR_BUCKET/products/*",
        "arn:aws:s3:::YOUR_BUCKET/packages/*"
      ]
    }
  ]
}
```

Presigned URL mang quyền của credential đã ký: nếu credential hiện tại chỉ có quyền đọc (trước đây backend chỉ verify),
`PUT` sẽ bị S3 từ chối `AccessDenied` cho đến khi bổ sung `s3:PutObject`. Nếu bucket dùng SSE-KMS, credential còn cần
`kms:GenerateDataKey` (upload) và `kms:Decrypt` (đọc) trên key đó. Nếu đã có policy riêng cho snapshot mô phỏng hoặc RAG, giữ nguyên.

## 10b. Chẩn đoán "Không tải được tệp lên" của kỹ thuật viên (bước 2)

Chuỗi mã (`FileUploadUrlService` → `S3StorageUploadUrlSigner` → FE `fileService.ts` PUT bằng axios trần, đúng `headers` được
cấp) được kiểm trong `S3CompatibleUploadChainTests` với một server S3-compatible dùng một lần (`SMARTSOLAR_TEST_S3=endpoint|accessKey|secretKey[|origin]`):
JPG / PNG / WebP / PDF hợp lệ đi hết chuỗi và `VERIFIED` có `objectVersionId`; đổi Content-Type hoặc link hết hạn → 403;
preflight `OPTIONS` có `Access-Control-Allow-Origin`; sai MIME / sai cỡ → `REJECTED`; quá cỡ → `FILE_TOO_LARGE`; tệp
chưa `VERIFIED` không gắn được vào khảo sát (`FILE_NOT_VERIFIED`). Lỗi ở production vì vậy nằm ở **cấu hình kho**, và chỉ
kết luận khi có request / response thật. Lấy từ tab Network của trình duyệt kỹ thuật viên (ẩn chữ ký trước khi chia sẻ):

1. Response của `POST /api/files/presigned-upload`: host trong `uploadUrl` phải là endpoint **đúng vùng** của bucket
   (`https://<bucket>.s3.<region>.amazonaws.com/…`). Vùng khác → S3 trả `301 PermanentRedirect` **không kèm CORS** → axios báo
   `status 0`. Sửa `Storage__S3__Region`.
2. Request `OPTIONS` (preflight) tới `uploadUrl`: cần `200/204` kèm `Access-Control-Allow-Origin` = origin FE và
   `Access-Control-Allow-Methods` chứa `PUT`. Thiếu → cấu hình CORS bucket (mục 10). Lưu ý preflight tới endpoint sai vùng
   vẫn có thể 200 nên kiểm bước 1 trước.
3. Request `PUT`: `403 SignatureDoesNotMatch` → header gửi khác `headers` được cấp (Content-Type sửa bởi interceptor, thêm
   `Authorization`); `403 AccessDenied` → IAM thiếu `s3:PutObject` trên tiền tố `onsite-surveys/*`, hoặc SSE-KMS thiếu
   `kms:GenerateDataKey`; `403 Request has expired` → quá `Storage__S3__PresignedUrlTtlSeconds` (mạng chậm với tệp lớn, tăng TTL
   trong 30–3600 giây); `400 EntityTooLarge` không xảy ra với presigned PUT (cỡ kiểm ở bước 3).
4. `POST /api/files` trả `REJECTED`: `rejectionReason` nói rõ (không tồn tại / sai cỡ / sai loại); `PENDING_VERIFICATION` lặp lại
   → backend không đọc được object (IAM `s3:GetObject`, `s3:GetObjectVersion`, hoặc bucket chưa bật Versioning khi
   `Storage__S3__RequireObjectVersioning=true`).
5. `POST …/onsite-survey/attachments` 409 `FILE_NOT_VERIFIED` → FE gắn tệp chưa `VERIFIED`; hoàn thành khảo sát cần một ảnh
   `ROOF` đã gắn.

## 11. Giới hạn đã biết

* Presigned PUT không giới hạn dung lượng phía S3; dung lượng thật được kiểm tra ở bước 3 (khác `sizeBytes` → `REJECTED`).
* Object upload mà không đăng ký (bỏ dở ở bước 3) vẫn nằm trong bucket; nên đặt S3 Lifecycle rule xóa object cũ không dùng nếu cần.
* Chưa smoke-test với bucket AWS thật trong lần thay đổi này (xem checklist ở `integration-smoke-test-plan.md` khi triển khai).
