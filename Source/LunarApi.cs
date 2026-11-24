// Публичный API LunarEvents для соседних клиентских модов (DifRUST).
// Никаких ссылок на внутренние типы: только примитивы и строка.
// Версия растёт, если меняется поведение или состав методов.
public static class LunarEventsApi
{
    public const int Version = 1;

    // Особый ид луны на ночь nightDay мира с сидом seed.
    // "" — обычная ночь. Ид: lunar_harvest, lunar_craft, ..., lunar_fatal.
    // Детерминированно: клиент считает то же, что и сервер.
    public static string SpecialForNight(int seed, int nightDay)
    {
        return LunarCore.SpecialForNight(seed, nightDay) ?? "";
    }
}
