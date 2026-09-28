// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace BannerlordFG.Core
{
    // Stable in-module fallback. Native Bannerlord TextObject translations should be used
    // for production when its language-id mapping for the installed version is known.
    public static class Locale
    {
        private static readonly Dictionary<string, string[]> Strings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Section, on, off, multiplier, base cap, managed, unlimited, requires restart
            ["English"] = new[]{"Frame Generation","On","Off","Multiplier","Base FPS Limit","Managed by BannerlordFG","Unlimited","Restart required"},
            ["Português (BR)"] = new[]{"Geração de Quadros","Ligado","Desligado","Multiplicador","Limite de FPS base","Gerenciado pelo BannerlordFG","Ilimitado","Reinicialização necessária"},
            ["简体中文"] = new[]{"帧生成","开启","关闭","倍数","基础帧率限制","由 BannerlordFG 管理","无限制","需要重新启动"},
            ["繁體中文"] = new[]{"影格生成","開啟","關閉","倍數","基礎影格率限制","由 BannerlordFG 管理","無限制","需要重新啟動"},
            ["Deutsch"] = new[]{"Frame-Generierung","Ein","Aus","Multiplikator","Basis-FPS-Limit","Von BannerlordFG verwaltet","Unbegrenzt","Neustart erforderlich"},
            ["Français"] = new[]{"Génération d'images","Activé","Désactivé","Multiplicateur","Limite des FPS de base","Géré par BannerlordFG","Illimité","Redémarrage requis"},
            ["Italiano"] = new[]{"Generazione dei fotogrammi","Attiva","Disattiva","Moltiplicatore","Limite FPS di base","Gestito da BannerlordFG","Illimitato","Riavvio necessario"},
            ["日本語"] = new[]{"フレーム生成","オン","オフ","倍率","ベースFPS制限","BannerlordFG が管理","無制限","再起動が必要"},
            ["한국어"] = new[]{"프레임 생성","켜기","끄기","배수","기본 FPS 제한","BannerlordFG에서 관리","무제한","다시 시작 필요"},
            ["Polski"] = new[]{"Generowanie klatek","Wł.","Wył.","Mnożnik","Limit bazowych FPS","Zarządzane przez BannerlordFG","Bez limitu","Wymagany restart"},
            ["Русский"] = new[]{"Генерация кадров","Вкл.","Выкл.","Множитель","Лимит базового FPS","Управляется BannerlordFG","Без ограничений","Требуется перезапуск"},
            ["Español (LA)"] = new[]{"Generación de fotogramas","Activado","Desactivado","Multiplicador","Límite de FPS base","Administrado por BannerlordFG","Ilimitado","Requiere reinicio"},
            ["Türkçe"] = new[]{"Kare Üretimi","Açık","Kapalı","Çarpan","Temel FPS sınırı","BannerlordFG tarafından yönetiliyor","Sınırsız","Yeniden başlatma gerekli"}
        };
        public static string Get(string displayLanguage, int index) => Strings.TryGetValue(displayLanguage ?? "", out var values) ? values[index] : Strings["English"][index];
        public static int Count => Strings.Count;
    }
}
