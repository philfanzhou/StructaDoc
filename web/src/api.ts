export type Session = { authenticated: boolean; subjectType?: string; subjectId?: string; issuer?: string; displayName?: string; email?: string; isAdministrator: boolean; oidcEnabled: boolean; setupRequired: boolean }
export type DocumentItem = { id: string; originalFileName: string; mediaType: string; extension: string; sizeBytes: number; sha256: string; createdAt: string; latestParseStatus?: string; ownedByCurrentUser: boolean }
export type ParseRun = { id: string; documentId: string; status: string; stage?: string; providerType: string; attemptCount: number; maxAttempts: number; errorCode?: string; errorMessage?: string; createdAt: string; completedAt?: string }
export type ParseBlock = { id: string; sequence: number; pageNumber?: number; type: string; subtype?: string; content?: string; contentFormat?: string; confidence?: number; assetId?: string }
export type ParseExecutionStatus = { workerEnabled: boolean; executionEnabled: boolean }

let csrf: { requestToken: string; headerName: string } | undefined

async function problem(response: Response): Promise<never> {
  const body = await response.json().catch(() => ({})) as { title?: string; detail?: string; errors?: Record<string, string[]> }
  const validation = body.errors ? Object.values(body.errors).flat().join(' ') : ''
  throw new Error(validation || body.detail || body.title || `请求失败（${response.status}）`)
}

export async function antiforgery() {
  if (!csrf) csrf = await get('/api/v1/admin/antiforgery')
  return csrf!
}

export function resetAntiforgery() { csrf = undefined }

export async function get<T = any>(url: string): Promise<T> {
  const response = await fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
  if (!response.ok) return problem(response)
  return response.status === 204 ? undefined as T : response.json()
}

export async function mutate<T = any>(url: string, method: string, body?: unknown): Promise<T> {
  const token = await antiforgery()
  const response = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', [token.headerName]: token.requestToken },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!response.ok) return problem(response)
  return response.status === 204 ? undefined as T : response.json()
}

export async function upload(file: File): Promise<DocumentItem> {
  const token = await antiforgery()
  const data = new FormData(); data.append('file', file)
  const response = await fetch('/api/v1/documents', { method: 'POST', credentials: 'same-origin', headers: { [token.headerName]: token.requestToken }, body: data })
  if (!response.ok) return problem(response)
  return response.json()
}
