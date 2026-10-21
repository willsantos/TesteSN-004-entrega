using TechJobs.Api.JobsCore;

namespace TechJobs.Api.Operations;

/// <summary>
/// Seed determinístico de vagas sintéticas: mesmo (count, seed) produz exatamente o mesmo
/// conteúdo — ids, títulos, empresas, descrições, techs, salários, status e datas (base
/// temporal fixa em 2026-01-01 UTC). Usa System.Random com seed explícito (determinístico
/// entre execuções na mesma versão do .NET).
/// </summary>
public static class SeedGenerator
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Roles =
    [
        "Desenvolvedor(a) Backend", "Desenvolvedor(a) Frontend", "Desenvolvedor(a) Full-Stack",
        "Engenheiro(a) de Software", "Engenheiro(a) de Dados", "Engenheiro(a) de DevOps",
        "Analista de Sistemas", "Analista de QA", "Arquiteto(a) de Software",
        "Cientista de Dados", "Desenvolvedor(a) Mobile", "Engenheiro(a) de Plataforma",
    ];

    private static readonly string[] Companies =
    [
        "Acme Tech", "Inova Sistemas", "TechNova", "Código Bom", "Dados & Cia", "CloudBrasil",
        "ByteForge", "Lógica Digital", "SofTech", "Aurora Software", "Vetor Sistemas",
        "Núcleo Digital", "Prisma Tecnologia", "Quanta Systems", "Orbita Labs", "Helix TI",
        "Fenix Software", "Atlas Computing", "Nimbus Tech", "Vértice Digital", "Polaris Dev",
        "Andes Systems", "Sirius TI", "Kraken Software", "Meridian Labs", "Solaris Tech",
        "Cometa Digital", "Íris Sistemas", "Zenit Tecnologia", "Órion Software",
        "Delta Sistemas", "Kairos Labs", "Éter Digital", "Aurum Tech", "Basalto TI",
        "Coral Software", "Duna Sistemas", "Elétron Labs", "Fôlego Tech", "Gema Digital",
    ];

    private static readonly string[] Technologies =
    [
        "C#", ".NET", "Java", "Spring Boot", "Python", "Django", "Node.js", "TypeScript",
        "React", "Angular", "Vue.js", "SQL Server", "PostgreSQL", "MySQL", "MongoDB",
        "Redis", "Elasticsearch", "Docker", "Kubernetes", "AWS", "Azure", "GCP",
        "RabbitMQ", "Kafka", "Go", "Rust", "PHP", "Laravel", "Ruby on Rails",
    ];

    private static readonly string?[] Cities =
    [
        "São Paulo", "Rio de Janeiro", "Belo Horizonte", "Curitiba", "Porto Alegre",
        "Florianópolis", "Recife", "Salvador", "Brasília", "Fortaleza", "Campinas",
        "Goiânia", null, null, null,
    ];

    private static readonly (decimal Base, decimal Spread)[] SalaryBySeniority =
    [
        (3500m, 3000m),  // junior
        (6500m, 5500m),  // pleno
        (12000m, 10000m), // senior
    ];

    private static readonly string[] SeniorityWord = ["Júnior", "Pleno", "Sênior"];

    public static IReadOnlyList<Job> Generate(int count, int seed)
    {
        var rnd = new Random(seed);
        var jobs = new List<Job>(count);

        for (var i = 0; i < count; i++)
        {
            var seniority = PickWeighted(rnd, (Seniority.Junior, 30), (Seniority.Pleno, 45), (Seniority.Senior, 25));
            var workMode = PickWeighted(rnd, (WorkMode.Remoto, 45), (WorkMode.Hibrido, 35), (WorkMode.Presencial, 20));
            var role = Roles[rnd.Next(Roles.Length)];
            var company = Companies[rnd.Next(Companies.Length)];
            var city = Cities[rnd.Next(Cities.Length)];
            if (workMode == WorkMode.Remoto && rnd.NextDouble() < 0.6) city = null;

            var techCount = 2 + rnd.Next(5); // 2 a 6 tecnologias
            var techs = new List<string>(techCount);
            while (techs.Count < techCount)
            {
                var tech = Technologies[rnd.Next(Technologies.Length)];
                if (!techs.Contains(tech, StringComparer.OrdinalIgnoreCase)) techs.Add(tech);
            }

            var title = $"{role} {techs[0]} {SeniorityWord[(int)seniority]}";

            decimal? salaryMin = null, salaryMax = null;
            if (rnd.NextDouble() < 0.8) // ~20% das vagas sem salário
            {
                var (baseSalary, spread) = SalaryBySeniority[(int)seniority];
                var jitter = (decimal)rnd.NextDouble() * spread;
                var min = Math.Round(baseSalary + jitter, 2);
                var range = Math.Round(min * (0.15m + (decimal)rnd.NextDouble() * 0.35m), 2);
                salaryMin = min;
                salaryMax = min + range;
            }

            var status = rnd.NextDouble() < 0.12 ? JobStatus.Encerrada : JobStatus.Aberta; // ~12% encerradas
            var publishedAt = Epoch.AddDays(rnd.Next(365)).AddHours(rnd.Next(24)).AddMinutes(rnd.Next(60));

            var description =
                $"Vaga para {title.ToLower()} na {company}. Atuação com {string.Join(", ", techs.Take(3))} " +
                $"em times ágeis, com revisão de código, testes automatizados e boas práticas de engenharia. " +
                $"Buscamos profissional {SeniorityWord[(int)seniority].ToLower()} com comunicação eficaz, " +
                $"interesse em arquitetura e vontade de aprender tecnologias modernas.";

            jobs.Add(new Job(
                DeterministicGuid(rnd), title, company, description, techs,
                seniority, workMode, city, salaryMin, salaryMax, status,
                publishedAt, publishedAt));
        }
        return jobs;
    }

    private static T PickWeighted<T>(Random rnd, params (T Value, int Weight)[] options)
    {
        var total = options.Sum(o => o.Weight);
        var pick = rnd.Next(total);
        foreach (var (value, weight) in options)
        {
            if (pick < weight) return value;
            pick -= weight;
        }
        return options[^1].Value;
    }

    private static Guid DeterministicGuid(Random rnd)
    {
        var bytes = new byte[16];
        rnd.NextBytes(bytes);
        bytes[7] &= 0x0F; // clareza de versão não é exigida; apenas estabilidade
        return new Guid(bytes);
    }
}
