import { ref } from 'vue'
import { get, type Session } from './api'

export const session = ref<Session>()

export async function loadSession(): Promise<Session> {
  session.value = await get<Session>('/api/v1/session')
  return session.value
}

export async function ensureSession(): Promise<Session> {
  return session.value ?? await loadSession()
}

// Mirrors the Host's NormalizeReturnUrl: only application-relative single-slash paths.
export function safeReturnUrl(value: unknown, fallback: string): string {
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') ? value : fallback
}
