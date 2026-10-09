import { vi } from 'vitest'

export interface Call {
  method: string
  url: string
  body: unknown
}

type Handler = (call: Call) => Response | Promise<Response>

export function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

// Подменяет fetch: каждый вызов записывается и отдаётся обработчику
export function mockFetch(handler: Handler): Call[] {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: string, init?: RequestInit) => {
    const call = {
      method: init?.method ?? 'GET',
      url: String(input),
      body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
    }
    calls.push(call)
    return handler(call)
  }))
  return calls
}
