import { describe, expect, it } from 'vitest'
import { ApiError, ValidationError, toError } from './client'

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
