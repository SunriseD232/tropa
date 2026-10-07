namespace Tropa.Core.Routing;

/// <summary>
/// Российские сервисы, которые не пускают с зарубежных IP или требуют дополнительной проверки.
/// Всегда идут напрямую раньше всех остальных правил (info.ru.json: ruServices).
/// </summary>
public static class RuServices
{
    public static IReadOnlyList<string> DomainSuffixes { get; } =
    [
        // Государство
        "gosuslugi.ru", "esia.gosuslugi.ru", "nalog.gov.ru", "nalog.ru", "mos.ru", "pfr.gov.ru", "sfr.gov.ru",
        "cbr.ru", "kremlin.ru", "government.ru", "fss.ru", "rosreestr.gov.ru", "gibdd.ru",
        // Банки и платежи
        "sberbank.ru", "sber.ru", "tbank.ru", "tinkoff.ru", "vtb.ru", "alfabank.ru", "gazprombank.ru",
        "raiffeisen.ru", "pochtabank.ru", "sovcombank.ru", "open.ru", "mkb.ru", "psbank.ru", "rshb.ru",
        "domrf.ru", "rosbank.ru", "nspk.ru", "mironline.ru", "sbp.nspk.ru", "yoomoney.ru",
        // Маркетплейсы и сервисы
        "ozon.ru", "wildberries.ru", "wb.ru", "avito.ru", "market.yandex.ru",
    ];
}
