import type { Run, RunStatus, Scenario } from './types'

const BASE = '/api'

// Ошибки полей из ValidationProblem координатора: { "steps[1].target_rps": ["..."] }
export type FieldErrors = Record<string, string[]>

export class ValidationError extends Error {
  readonly fields: FieldErrors

  constructor(fields: FieldErrors) {
    super('Сценарий не прошёл проверку')
    this.fields = fields
  }
}

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

export async function createRun(scenario: Scenario): Promise<{ id: string; status: RunStatus }> {
  const response = await fetch(`${BASE}/runs`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(scenario),
  })
  if (response.status === 201)
    return response.json()
  throw await toError(response)
}

export async function getRun(id: string, signal?: AbortSignal): Promise<Run> {
  const response = await fetch(`${BASE}/runs/${encodeURIComponent(id)}`, { signal })
  if (response.ok)
    return response.json()
  if (response.status === 404)
    throw new ApiError(404, 'Прогон не найден')
  throw await toError(response)
}

export async function toError(response: Response): Promise<Error> {
  const body = await readJson(response)
  const errors = body?.errors
  if (response.status === 400 && isFieldErrors(errors))
    return new ValidationError(errors)

  const detail = typeof body?.detail === 'string' ? body.detail
    : typeof body?.title === 'string' ? body.title
    : null
  return new ApiError(response.status, detail ?? `Координатор ответил ${response.status}`)
}

async function readJson(response: Response): Promise<Record<string, unknown> | null> {
  try {
    const body = await response.json()
    return body !== null && typeof body === 'object' ? body : null
  } catch {
    return null
  }
}

function isFieldErrors(value: unknown): value is FieldErrors {
  return value !== null && typeof value === 'object'
    && Object.values(value).every((messages) =>
      Array.isArray(messages) && messages.every((m) => typeof m === 'string'))
}
