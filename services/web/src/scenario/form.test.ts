import { describe, expect, it } from 'vitest'
import { MAX_STEPS, buildSteps, defaultForm, formFromScenario, mapServerErrors, toScenario, totalSeconds } from './form'

describe('buildSteps', () => {
  it('собирает ступени как в examples/scenario.yaml', () => {
    const result = buildSteps(defaultForm)
    expect(result.ok).toBe(true)
    if (!result.ok)
      return
    expect(result.steps.map((s) => s.target_rps)).toEqual([100, 150, 200, 250, 300, 350, 400])
    expect(result.steps.every((s) => s.duration_seconds === 45)).toBe(true)
    expect(totalSeconds(result.steps)).toBe(315)
  })

  it('не выходит за верхнюю границу, если шаг её не делит', () => {
    const result = buildSteps({ ...defaultForm, fromRps: '100', toRps: '220', stepRps: '50' })
    expect(result.ok && result.steps.map((s) => s.target_rps)).toEqual([100, 150, 200])
  })

  it('дробный шаг не копит погрешность', () => {
    const result = buildSteps({ ...defaultForm, fromRps: '0.1', toRps: '1', stepRps: '0.1' })
    expect(result.ok && result.steps.map((s) => s.target_rps)).toEqual(
      [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1])
  })

  it('принимает запятую как десятичный разделитель', () => {
    const result = buildSteps({ ...defaultForm, durationSeconds: '2,5' })
    expect(result.ok && result.steps[0].duration_seconds).toBe(2.5)
  })

  it('одна ступень, если от и до совпадают', () => {
    const result = buildSteps({ ...defaultForm, fromRps: '100', toRps: '100' })
    expect(result.ok && result.steps).toEqual([{ target_rps: 100, duration_seconds: 45 }])
  })

  it.each(['', '0', '-5', 'abc', 'Infinity'])('отклоняет значение %j', (value) => {
    const result = buildSteps({ ...defaultForm, stepRps: value })
    expect(result).toEqual({ ok: false, errors: { stepRps: 'Нужно число больше нуля' } })
  })

  it('верхняя граница не меньше нижней', () => {
    const result = buildSteps({ ...defaultForm, fromRps: '300', toRps: '200' })
    expect(result.ok).toBe(false)
    expect(!result.ok && result.errors.toRps).toBeTruthy()
  })

  it('ограничивает число ступеней', () => {
    const result = buildSteps({ ...defaultForm, fromRps: '1', toRps: '1000', stepRps: '1' })
    expect(result.ok).toBe(false)
    expect(!result.ok && result.errors.stepRps).toContain(String(MAX_STEPS))
  })
})

describe('toScenario', () => {
  it('обрезает пробелы в имени и URL', () => {
    const scenario = toScenario({ ...defaultForm, name: '  ramp ', url: ' http://abstock:8080/ ' }, [])
    expect(scenario).toEqual({ name: 'ramp', target: { url: 'http://abstock:8080/', method: 'GET' }, steps: [] })
  })
})

describe('mapServerErrors', () => {
  it('раскладывает ошибки координатора по полям формы', () => {
    const errors = mapServerErrors({
      'name': ['Имя сценария не задано'],
      'target.url': ['Нужен абсолютный URL с http или https'],
      'target.method': ['Допустимы GET'],
      'steps[2].target_rps': ['Нагрузка должна расти: 150 после 200'],
      'steps[0].duration_seconds': ['Должно быть конечным числом больше нуля'],
      'body': ['Некорректный JSON'],
    })
    expect(errors).toEqual({
      name: 'Имя сценария не задано',
      url: 'Нужен абсолютный URL с http или https',
      method: 'Допустимы GET',
      stepRps: 'Ступень 3: Нагрузка должна расти: 150 после 200',
      durationSeconds: 'Должно быть конечным числом больше нуля',
      form: 'body: Некорректный JSON',
    })
  })

  it('оставляет первую ошибку, если на поле их несколько', () => {
    const errors = mapServerErrors({
      'steps[0].target_rps': ['первая'],
      'steps[1].target_rps': ['вторая'],
    })
    expect(errors).toEqual({ stepRps: 'Ступень 1: первая' })
  })

  it('ошибка всего списка ступеней без номера', () => {
    expect(mapServerErrors({ steps: ['Нужна хотя бы одна ступень'] }))
      .toEqual({ stepRps: 'Нужна хотя бы одна ступень' })
  })
})

describe('formFromScenario', () => {
  const built = buildSteps(defaultForm)
  const scenario = toScenario(defaultForm, built.ok ? built.steps : [])

  it('равномерные ступени восстанавливаются точно', () => {
    const { form, exact } = formFromScenario(scenario)
    expect(exact).toBe(true)
    expect(form).toEqual(defaultForm)
  })

  it('неравномерные ступени: по первой и последней, exact = false', () => {
    const uneven = { ...scenario, steps: [100, 150, 400].map((target_rps) => ({ target_rps, duration_seconds: 30 })) }
    const { form, exact } = formFromScenario(uneven)
    expect(exact).toBe(false)
    expect(form).toMatchObject({ fromRps: '100', toRps: '400', stepRps: '50', durationSeconds: '30' })
  })

  it('разная длительность ступеней тоже не точная', () => {
    const mixed = { ...scenario, steps: [{ target_rps: 100, duration_seconds: 30 }, { target_rps: 200, duration_seconds: 60 }] }
    expect(formFromScenario(mixed).exact).toBe(false)
  })

  it('одна ступень', () => {
    const single = { ...scenario, steps: [{ target_rps: 250, duration_seconds: 10 }] }
    const { form, exact } = formFromScenario(single)
    expect(exact).toBe(true)
    expect(form).toMatchObject({ fromRps: '250', toRps: '250', durationSeconds: '10' })
  })
})
