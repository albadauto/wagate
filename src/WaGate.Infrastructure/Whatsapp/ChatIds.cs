namespace WaGate.Infrastructure.Whatsapp;

public static class ChatIds
{
    /// <summary>
    /// Converte telefone (com ou sem máscara) para chatId do WhatsApp.
    /// Ids que já contêm '@' (grupos, @lid, @newsletter) são mantidos.
    /// </summary>
    public static string ToChatId(string phoneOrChatId)
    {
        if (string.IsNullOrWhiteSpace(phoneOrChatId))
            throw new ArgumentException("Telefone obrigatório.", nameof(phoneOrChatId));

        var v = phoneOrChatId.Trim();
        if (v.Contains('@')) return v;
        if (v.EndsWith("-group", StringComparison.OrdinalIgnoreCase)) return v[..^6] + "@g.us";

        var digits = new string(v.Where(char.IsDigit).ToArray());
        if (digits.Length < 8)
            throw new ArgumentException($"Telefone inválido: '{phoneOrChatId}'.", nameof(phoneOrChatId));
        return digits + "@c.us";
    }

    /// <summary>Extrai somente o número de um chatId (5511...@c.us → 5511...).</summary>
    public static string? ToPhone(string? chatId)
    {
        if (string.IsNullOrEmpty(chatId)) return null;
        var at = chatId.IndexOf('@');
        var user = at >= 0 ? chatId[..at] : chatId;
        var colon = user.IndexOf(':'); // device suffix (5511...:12@s.whatsapp.net)
        if (colon >= 0) user = user[..colon];
        return user;
    }

    public static bool IsGroup(string? chatId) => chatId?.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) == true;
}
