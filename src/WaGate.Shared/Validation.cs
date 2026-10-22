using System.ComponentModel.DataAnnotations;

namespace WaGate.Shared;

/// <summary>Validações brasileiras compartilhadas entre API e painel.</summary>
public static class BrDocs
{
    public static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    public static bool IsCpf(string? value)
    {
        var d = Digits(value);
        if (d.Length != 11 || d.Distinct().Count() == 1) return false;
        int Calc(int len)
        {
            var sum = 0;
            for (var i = 0; i < len; i++) sum += (d[i] - '0') * (len + 1 - i);
            var r = sum * 10 % 11;
            return r == 10 ? 0 : r;
        }
        return Calc(9) == d[9] - '0' && Calc(10) == d[10] - '0';
    }

    public static bool IsCnpj(string? value)
    {
        var d = Digits(value);
        if (d.Length != 14 || d.Distinct().Count() == 1) return false;
        int[] w1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] w2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int Calc(int[] w)
        {
            var sum = 0;
            for (var i = 0; i < w.Length; i++) sum += (d[i] - '0') * w[i];
            var r = sum % 11;
            return r < 2 ? 0 : 11 - r;
        }
        return Calc(w1) == d[12] - '0' && Calc(w2) == d[13] - '0';
    }

    public static bool IsCpfOrCnpj(string? value) => IsCpf(value) || IsCnpj(value);

    /// <summary>Celular/fixo brasileiro com DDD (10 ou 11 dígitos), opcionalmente com 55 na frente.</summary>
    public static bool IsBrPhone(string? value)
    {
        var d = Digits(value);
        if (d.StartsWith("55") && d.Length is 12 or 13) d = d[2..];
        if (d.Length is not (10 or 11)) return false;
        if (d[0] == '0' || d[1] == '0') return false;                // DDD inválido
        return d.Length == 10 || d[2] == '9';                        // celular começa com 9
    }

    /// <summary>Telefone no formato E.164 sem "+" (55 + DDD + número).</summary>
    public static string NormalizePhone(string value)
    {
        var d = Digits(value);
        return d.StartsWith("55") && d.Length is 12 or 13 ? d : "55" + d;
    }

    public static string FormatDocument(string? value)
    {
        var d = Digits(value);
        return d.Length switch
        {
            11 => $"{d[..3]}.{d[3..6]}.{d[6..9]}-{d[9..]}",
            14 => $"{d[..2]}.{d[2..5]}.{d[5..8]}/{d[8..12]}-{d[12..]}",
            _ => value ?? ""
        };
    }

    /// <summary>Regras de senha: 8+ caracteres, maiúscula, minúscula e número. Retorna a lista do que falta.</summary>
    public static IReadOnlyList<string> PasswordIssues(string? p)
    {
        p ??= "";
        var issues = new List<string>();
        if (p.Length < 8) issues.Add("ter pelo menos 8 caracteres");
        if (!p.Any(char.IsUpper)) issues.Add("ter uma letra maiúscula");
        if (!p.Any(char.IsLower)) issues.Add("ter uma letra minúscula");
        if (!p.Any(char.IsDigit)) issues.Add("ter um número");
        return issues;
    }

    /// <summary>Força de 0 a 4 (para o medidor do formulário).</summary>
    public static int PasswordScore(string? p)
    {
        if (string.IsNullOrEmpty(p)) return 0;
        var score = 4 - PasswordIssues(p).Count;
        if (p.Length >= 12 && p.Any(c => !char.IsLetterOrDigit(c))) score++;
        return Math.Clamp(score, 0, 4);
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class CpfCnpjAttribute : ValidationAttribute
{
    public CpfCnpjAttribute() : base("CPF ou CNPJ inválido.") { }
    public override bool IsValid(object? value) => value is not string s || string.IsNullOrWhiteSpace(s) || BrDocs.IsCpfOrCnpj(s);
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class BrPhoneAttribute : ValidationAttribute
{
    public BrPhoneAttribute() : base("Informe um telefone com DDD, ex.: (11) 99999-9999.") { }
    public override bool IsValid(object? value) => value is not string s || string.IsNullOrWhiteSpace(s) || BrDocs.IsBrPhone(s);
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class StrongPasswordAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext ctx)
    {
        var issues = BrDocs.PasswordIssues(value as string);
        return issues.Count == 0
            ? ValidationResult.Success
            : new ValidationResult("A senha precisa " + string.Join(", ", issues) + ".", [ctx.MemberName!]);
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class MustBeTrueAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is true;
}
