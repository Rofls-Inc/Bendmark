import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, UNAVAILABLE_MESSAGE, ValidationError, listRuns, request, toError } from './client'

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json' } })

describe('toError', () => {
  it('400 с ошибками полей превращается в ValidationError', async () => {
    const error = await toError(json(400, {
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { 'steps[1].target_rps': ['Неверный тип значения'] },
    }))
    expect(error).toBeInstanceOf(ValidationError)
    expect((error as ValidationError).fields).toEqual({ 'steps[1].target_rps': ['Неверный тип значения'] })
  })

  it('415 отдаёт detail из ProblemDetails', async () => {
    const error = await toError(json(415, { detail: 'Ожидается Content-Type application/json или application/yaml' }))
    expect(error).toBeInstanceOf(ApiError)
    expect(error.message).toBe('Ожидается Content-Type application/json или application/yaml')
    expect((error as ApiError).status).toBe(415)
  })

  it('400 без errors не считается ошибкой полей', async () => {
    const error = await toError(json(400, { title: 'Bad Request' }))
    expect(error).toBeInstanceOf(ApiError)
    expect(error.message).toBe('Bad Request')
  })

  it('ответ не в JSON, например от прокси', async () => {
    const error = await toError(new Response('<html>Bad Gateway</html>', { status: 502 }))
    expect(error).toBeInstanceOf(ApiError)
    expect(error.message).toBe('Координатор ответил 502')
  })
})

describe('request', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('сетевая ошибка превращается в «Координатор недоступен»', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    const error = await request('/api/runs').catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).message).toBe(UNAVAILABLE_MESSAGE)
  })

  it('отмену запроса не подменяет', async () => {
    const abort = new DOMException('aborted', 'AbortError')
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(abort))
    await expect(request('/api/runs/1')).rejects.toBe(abort)
  })
})

describe('listRuns', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('передаёт limit и before', async () => {
    const fetch = vi.fn().mockResolvedValue(json(200, { runs: [], next_before: null }))
    vi.stubGlobal('fetch', fetch)
    await listRuns({ limit: 5, before: 'abc' })
    expect(fetch.mock.calls[0][0]).toBe('/api/runs?limit=5&before=abc')
  })

  it('400 на неизвестный before отдаёт текст координатора', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(400, { detail: 'Прогон abc не найден' })))
    await expect(listRuns({ before: 'abc' })).rejects.toThrow('Прогон abc не найден')
  })

  it('ответ без runs - ошибка, а не пустой список', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(200, { id: 'x' })))
    await expect(listRuns()).rejects.toBeInstanceOf(ApiError)
  })
})
