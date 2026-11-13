using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AliceDesk {
    internal static class VoiceExport {
        internal static string Package(IEnumerable<DeskAction> actions,Profile profile) {
            string pair=profile.Get("local.agent.pair");
            if(!Regex.IsMatch(pair,@"\A[a-f0-9]{32}\z"))throw new InvalidOperationException("Нет адресата Home Assistant. Откройте вкладку подключения.");
            var rules=actions.Where(a=>!String.IsNullOrEmpty(profile.Get("local.agent.approved."+a.Id)) && profile.Get("local.agent.approved."+a.Id)==MediaCommands.Fingerprint(a)).ToList();
            if(rules.Count==0)throw new InvalidOperationException("Сначала сохраните и разрешите хотя бы одно действие.");
            var aliases=new HashSet<string>();
            var yaml=new StringBuilder("# Пульт Станции: пакет для Home Assistant. Не содержит API-ключей.\n# Заменяет прежний пакет stantsiya_desk; не устанавливайте два пакета с теми же helpers.\ninput_text:\n  stantsiya_desk_command:\n    name: Команда Пульта Станции\n    max: 255\n    initial: idle\n  stantsiya_desk_reply:\n    name: Ответ Пульта Станции\n    max: 255\n    initial: idle\nautomation:\n  - id: stantsiya_desk_voice\n    alias: Пульт Станции — голосовые команды\n    mode: queued\n    max: 10\n    triggers:\n");
            foreach(var a in rules) {
                MediaCommands.Validate(a);
                yaml.Append("      - trigger: conversation\n        id: ").Append(Disk.Json(a.Id)).Append("\n        command:\n");
                foreach(string phrase in MediaCommands.Aliases(a.Phrases)) {
                    if(!Regex.IsMatch(phrase,@"\A[\p{L}\p{N} _-]+\z"))throw new InvalidOperationException("Для экспорта в Assist используйте слова без пунктуации и шаблонных скобок: «"+phrase+"».");
                    if(!aliases.Add(phrase))throw new InvalidOperationException("Фраза назначена дважды: "+phrase);
                    yaml.Append("          - ").Append(Disk.Json(phrase)).Append('\n');
                }
            }
            yaml.Append(@"    actions:
      - variables:
          request_id: ""{{ now().strftime('%Y%m%d%H%M%S%f') }}_{{ range(1000, 9999) | random }}""
      - action: input_text.set_value
        target:
          entity_id: input_text.stantsiya_desk_command
        data:
          value: >-
            {{ {'pair':'").Append(pair).Append(@"', 'id':request_id,
                'ts':as_timestamp(now())|int, 'action':trigger.id} | to_json }}
      - wait_template: ""{{ states('input_text.stantsiya_desk_reply').startswith(request_id ~ '|') }}""
        timeout: ""00:00:55""
        continue_on_timeout: true
      - set_conversation_response: >-
          {% if states('input_text.stantsiya_desk_reply') == request_id ~ '|ok' %}
            Команда принята плеером или компьютером.
          {% elif states('input_text.stantsiya_desk_reply') == request_id ~ '|denied' %}
            Команда отклонена. Проверь журнал и подключение Spotify в хабе.
          {% elif states('input_text.stantsiya_desk_reply') == request_id ~ '|unknown' %}
            Результат неизвестен. Проверь воспроизведение перед повтором.
          {% else %}
            Нет подтверждения от хаба. Проверь, включён ли компьютер.
          {% endif %}
");
            return yaml.ToString();
        }
    }
}
