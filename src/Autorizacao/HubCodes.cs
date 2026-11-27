namespace Chk3.Autorizacao;

/// <summary>
/// Códigos do Hub de Pagamentos (CheckoutPay), informados por Daniel em 27/11/2026.
/// Referência: docs/gateway/hub-pagamentos-codigos.md. O mapeamento status -> resultado é PROPOSTA do POC;
/// GATEWAY_APPROVED_CODES / GATEWAY_DECLINED_CODES (quando preenchidos) substituem os padrões.
/// </summary>
public static class HubCodes
{
    public sealed record Info(string Slug, string Descricao);

    /// <summary>Tag &lt;status&gt; da resposta processada.</summary>
    public static readonly IReadOnlyDictionary<string, Info> Status = new Dictionary<string, Info>
    {
        ["0"] = new("criada", "Criada / Em andamento"),
        ["1"] = new("autenticada", "Autenticada"),
        ["2"] = new("nao_autenticada", "Não-Autenticada"),
        ["3"] = new("autorizada", "Autorizada pela operadora"),
        ["4"] = new("nao_autorizada", "Não-Autorizada pela operadora"),
        ["5"] = new("em_cancelamento", "Em Cancelamento"),
        ["6"] = new("cancelado", "Cancelado"),
        ["7"] = new("em_captura", "Em Captura"),
        ["8"] = new("capturada", "Capturada / Finalizada"),
        ["9"] = new("nao_capturada", "Não-Capturada"),
        ["10"] = new("recorrente_agendado", "Pagamento Recorrente - Agendado"),
        ["16"] = new("nao_cancelada", "Não Cancelada"),
        ["19"] = new("autorizado_fraude", "Autorizado de fraude"),
        ["20"] = new("cancelado_fraude", "Cancelado por fraude"),
        ["21"] = new("nao_autorizada_erro", "Não Autorizada por erro"),
        ["22"] = new("recorrencia_finalizada", "Finalizado"),
        ["23"] = new("recorrencia_pausada", "Pausado"),
    };

    /// <summary>Erros do gateway (HTTP 409, &lt;errorCode&gt; / result/error/code).</summary>
    public static readonly IReadOnlyDictionary<string, Info> Errors = new Dictionary<string, Info>
    {
        ["001"] = new("xml_com_erros", "XML com erros"),
        ["010"] = new("auth_hub_invalida", "Autenticação no HUB inválida (MerchantId/MerchantKey)"),
        ["011"] = new("auth_cielo_invalida", "Autenticação na CIELO inválida"),
        ["012"] = new("auth_rede_invalida", "Autenticação na REDE inválida"),
        ["020"] = new("cartao_inconsistente_bpl", "Inconsistência no envio do cartão, verificar modalidade habilitada (BPL)"),
        ["021"] = new("modalidade_cielo", "Modalidade não habilitada na CIELO"),
        ["022"] = new("modalidade_rede", "Modalidade não habilitada na REDE"),
        ["023"] = new("parcelas_cielo", "Parcelas inválidas/limite CIELO"),
        ["024"] = new("parcelas_rede", "Parcelas inválidas/limite REDE"),
        ["025"] = new("cvv_invalido", "Código de segurança ausente ou inválido"),
        ["026"] = new("erro_processar_cartao", "Erro ao processar o cartão"),
        ["027"] = new("indicador_cvv_invalido", "Indicador do código de segurança inválido"),
        ["028"] = new("cartao_inconsistente", "Inconsistência no cartão (número ou validade)"),
        ["030"] = new("transacao_invalida", "Código de transação inválido"),
        ["031"] = new("tid_cielo_invalido", "TID inválido na CIELO"),
        ["032"] = new("transacao_rede_invalida", "Código de transação inválido na REDE"),
        ["033"] = new("falha_cancelar", "Falha ao cancelar"),
        ["034"] = new("ja_cancelada", "Transação já cancelada"),
        ["035"] = new("nao_encontrada", "Transação não encontrada para a operação"),
        ["036"] = new("ja_capturada", "Transação já capturada"),
        ["037"] = new("adquirente_nao_configurado", "Adquirente não configurado corretamente no painel"),
        ["100"] = new("timeout_operadora", "Falha ao conectar na operadora (timeout)"),
        ["900"] = new("moeda_invalida", "Moeda inválida (986)"),
        ["901"] = new("origem_invalida", "Idioma/origem inválido (BRA)"),
        ["902"] = new("data_invalida", "Data/Hora inválida (YYYY-MM-DD)"),
        ["903"] = new("pedido_invalido", "Número do pedido inválido (máx 50 alfanuméricos)"),
        ["904"] = new("valor_invalido", "Valor inválido"),
        ["905"] = new("nome_portador_invalido", "Caracteres inválidos no nome do portador"),
    };

    public static readonly string[] DefaultApproved = ["3", "7", "8"];       // 3; captura automática na V0: também 7 e 8
    public static readonly string[] DefaultDeclined = ["2", "4", "20", "21"];
    public static readonly string[] Pending = ["0", "1", "19"];              // indeterminada, Worker consulta

    /// <summary>Lista do .env (vírgulas) ou, vazia, o padrão do doc.</summary>
    public static HashSet<string> Codes(string? env, string[] fallback)
    {
        var l = (env ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. l.Length > 0 ? l : fallback];
    }

    public static Info? StatusInfo(string? code) => code is not null && Status.TryGetValue(code, out var i) ? i : null;

    /// <summary>Erro do gateway: "1" e "001" são o mesmo código. Fora da tabela (ex.: 1036 observado em 27/11) = null.</summary>
    public static Info? ErrorInfo(string? code) =>
        code is { Length: > 0 and <= 4 } && code.All(char.IsAsciiDigit) && Errors.TryGetValue(int.Parse(code).ToString("000"), out var i) ? i : null;

    /// <summary>Status processado que não é esperado na autorização (5, 6, 9, 10, 16, 22, 23 ou fora da tabela).</summary>
    public static bool Unexpected(string code, IReadOnlySet<string> approved, IReadOnlySet<string> declined) =>
        !approved.Contains(code) && !declined.Contains(code) && !Pending.Contains(code);

    /// <summary>Pedido aceito pelo Hub (erro 903): até 50 caracteres alfanuméricos, sem hífen.</summary>
    public static bool ValidOrderId(string? o) => o is { Length: >= 1 and <= 50 } && o.All(char.IsAsciiLetterOrDigit);
}
