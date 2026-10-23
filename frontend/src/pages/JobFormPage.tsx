import { useState } from 'react'
import { createJob, updateJob } from '../api'
import { ErrorBanner, Field, Spinner } from '../components/ui'
import { parseSalaryInput } from '../salary'
import type { Job, JobInput, JobStatus, Seniority, WorkMode } from '../types'

interface FormState {
  title: string
  company: string
  description: string
  technologies: string
  seniority: Seniority
  workMode: WorkMode
  city: string
  salaryMin: string
  salaryMax: string
  status: JobStatus
}

function fromJob(job: Job | null): FormState {
  return {
    title: job?.title ?? '',
    company: job?.company ?? '',
    description: job?.description ?? '',
    technologies: job?.technologies.join(', ') ?? '',
    seniority: job?.seniority ?? 'pleno',
    workMode: job?.workMode ?? 'remoto',
    city: job?.city ?? '',
    salaryMin: job?.salaryMin !== null && job?.salaryMin !== undefined ? String(job.salaryMin) : '',
    salaryMax: job?.salaryMax !== null && job?.salaryMax !== undefined ? String(job.salaryMax) : '',
    status: job?.status ?? 'aberta',
  }
}

function toInput(form: FormState, salaryMin: number | null, salaryMax: number | null): JobInput {
  return {
    title: form.title,
    company: form.company,
    description: form.description,
    technologies: form.technologies.split(',').map((t) => t.trim()).filter(Boolean),
    seniority: form.seniority,
    workMode: form.workMode,
    city: form.city.trim() === '' ? null : form.city.trim(),
    salaryMin,
    salaryMax,
    status: form.status,
  }
}

export function JobFormPage({
  editing, onSaved, onCancel,
}: {
  editing: Job | null
  onSaved: (job: Job) => void
  onCancel: () => void
}) {
  const [form, setForm] = useState<FormState>(() => fromJob(editing))
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const [salaryErrors, setSalaryErrors] = useState<{ salaryMin?: string; salaryMax?: string }>({})

  const set = (patch: Partial<FormState>) => setForm((f) => ({ ...f, ...patch }))

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    const min = parseSalaryInput(form.salaryMin)
    const max = parseSalaryInput(form.salaryMax)
    if (!min.ok || !max.ok) {
      setSalaryErrors({
        salaryMin: min.ok ? undefined : min.error,
        salaryMax: max.ok ? undefined : max.error,
      })
      return
    }
    setSalaryErrors({})
    setSaving(true)
    setError(null)
    try {
      const input = toInput(form, min.value, max.value)
      const saved = editing ? await updateJob(editing.id, input) : await createJob(input)
      onSaved(saved)
    } catch (err) {
      setError(err)
    } finally {
      setSaving(false)
    }
  }

  return (
    <div>
      <h1 className="page-title">{editing ? 'Editar vaga' : 'Cadastrar vaga'}</h1>

      {error !== null && <ErrorBanner error={error} />}
      {saving && <Spinner label="Salvando…" />}

      <form className="card form-grid" onSubmit={submit}>
        <Field label="Título (5 a 160 caracteres)">
          <input required minLength={5} maxLength={160} value={form.title} onChange={(e) => set({ title: e.target.value })} />
        </Field>
        <Field label="Empresa (1 a 120 caracteres)">
          <input required maxLength={120} value={form.company} onChange={(e) => set({ company: e.target.value })} />
        </Field>
        <label className="field span-all">
          Descrição (mínimo 30 caracteres)
          <textarea
            required
            minLength={30}
            rows={5}
            value={form.description}
            onChange={(e) => set({ description: e.target.value })}
            style={{ font: 'inherit', padding: 8, border: '1px solid var(--border)', borderRadius: 8 }}
          />
        </label>
        <Field label="Tecnologias (separadas por vírgula)">
          <input required value={form.technologies} onChange={(e) => set({ technologies: e.target.value })} placeholder="ex.: C#, .NET, Redis" />
        </Field>
        <Field label="Cidade (opcional)">
          <input value={form.city} onChange={(e) => set({ city: e.target.value })} placeholder="ex.: São Paulo" />
        </Field>
        <Field label="Senioridade">
          <select value={form.seniority} onChange={(e) => set({ seniority: e.target.value as Seniority })}>
            <option value="junior">Júnior</option>
            <option value="pleno">Pleno</option>
            <option value="senior">Sênior</option>
          </select>
        </Field>
        <Field label="Modalidade">
          <select value={form.workMode} onChange={(e) => set({ workMode: e.target.value as WorkMode })}>
            <option value="remoto">Remoto</option>
            <option value="hibrido">Híbrido</option>
            <option value="presencial">Presencial</option>
          </select>
        </Field>
        <Field label="Salário mínimo (R$, opcional)">
          <input
            inputMode="decimal"
            value={form.salaryMin}
            onChange={(e) => set({ salaryMin: e.target.value })}
            placeholder="ex.: 6500"
            aria-invalid={salaryErrors.salaryMin !== undefined || undefined}
          />
          {salaryErrors.salaryMin !== undefined && (
            <p className="field-error" role="alert">{salaryErrors.salaryMin}</p>
          )}
        </Field>
        <Field label="Salário máximo (R$, opcional)">
          <input
            inputMode="decimal"
            value={form.salaryMax}
            onChange={(e) => set({ salaryMax: e.target.value })}
            placeholder="ex.: 8500"
            aria-invalid={salaryErrors.salaryMax !== undefined || undefined}
          />
          {salaryErrors.salaryMax !== undefined && (
            <p className="field-error" role="alert">{salaryErrors.salaryMax}</p>
          )}
        </Field>
        <Field label="Situação">
          <select value={form.status} onChange={(e) => set({ status: e.target.value as JobStatus })}>
            <option value="aberta">Aberta</option>
            <option value="encerrada">Encerrada</option>
          </select>
        </Field>
        <div className="span-all form-actions">
          <button className="primary" type="submit" disabled={saving}>
            {editing ? 'Salvar alterações' : 'Cadastrar vaga'}
          </button>
          <button className="ghost" type="button" onClick={onCancel}>Cancelar</button>
        </div>
      </form>
    </div>
  )
}
