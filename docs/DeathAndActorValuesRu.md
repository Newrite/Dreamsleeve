# Смерть актёра и значения здоровья/магии/выносливости: события и точки записи

Исследование по IDA (read-only), 28 сентября 2026 года. Цель — решить, как клиенту
Dreamsleeve узнавать о смерти и об изменениях health/magicka/stamina: хуки или опрос.
Проверенные рантаймы: **SE 1.5.97 — полностью** (все адреса ниже, если не сказано иначе);
**AE 1.6.1170 — перекрёстно** по ключевым точкам (отправители TESDeathEvent и
TESEnterBleedoutEvent, вход в общую точку записи AV, число отправителей TESHitEvent);
**VR 1.4.15 — точечно** (те же три пункта). Адреса — VA образа (`0x140000000` + смещение),
ids — Address Library (SE id / AE id). Имена функций — из базы IDA или CommonLibSSE-NG.

## 1. TESDeathEvent

Структура `{ actorDying, actorKiller, bool dead }` (RE/T/TESDeathEvent.h), источник —
`ScriptEventSourceHolder` + 0x370. Синглтон: `GetSingleton` id 14108/14298
(SE `0x140186790`, статический объект `0x141EBF7B0`; AE `0x1401D5230`, объект `0x1420F7690`).

Отправителей ровно два, и они различаются флагом `dead`:

| Смысл | SE 1.5.97 | AE 1.6.1170 | VR 1.4.15 |
|---|---|---|---|
| `dead=false` — смерть началась | хелпер `0x14061E370` (id 37437), единственный вызов из `Actor::KillImpl` `0x140603B30` (id 36872, слот vtable 0x10E) | инлайн в `KillImpl` `0x1406972D0` (id 37896), байт `0` по `0x140698182` | `KillImpl` `0x14060C340` → хелпер `0x140626F60` |
| `dead=true` — смерть завершена | хелпер `0x14061E2C0` (id 37436), единственный вызов из `Actor::KillImmediate` `0x1405FB640` (id 36723) | инлайн в `0x14068E640` (id 37735), байт `1` по `0x14068E7B3` | `0x140603E30` → хелпер `0x140626EB0` |
| `BSTEventSource<TESDeathEvent>::SendEvent` | `0x14061B8B0` (id 37371) | `0x1406AD330` (id 38319) | — |

Цепочка от урона до события (SE):

1. `Character::damageav` `0x140621120` (id 37523) при `av==Health && value<0` зовёт виртуальный
   `Actor::HandleHealthDamage` `0x140627D50` (id 37653, слот 0x104).
2. `HandleHealthDamage`: если `!IsDead(false)` → `Character::sub_14062A530` `0x14062A530` (id 37677) —
   обработка essential/protected: при `fBleedoutMin`/`fBleedoutCheck` переводит в `kBleedout` (8)
   и возвращает 1; иначе, если health ≤ 0 → `Actor::Kill_impl2` `0x1405D4700` (id 36326).
3. `Kill_impl2`: записывает `myKiller`, кладёт задачу типа 10 в `BSTaskPool`
   (`0x1405C31B0` id 35936 → `0x1405C8FC0`, пул `qword_142F38978`). Единственный vcall слота 0x10E
   во всём образе — `BSTaskPool_HandleTask` `0x1405C6EE0` (id 36016): все убийства проходят через пул.
   Прямые вызовы `Kill_impl2`: HandleHealthDamage, sub_14062A530, Papyrus Kill/KillSilent
   (`0x14094B790`/`0x14094B760` → `0x14095D0E0` id 54332), console KillActor/KillAllActors,
   `KillActorHandler::Handle` `0x1407217B0` (id 41757, анимационное событие kill-move),
   ReanimateEffect/SummonCreatureEffect, FlameProjectile, `Actor::EndDeferredKill`.
4. `Actor::KillImpl` (`PlayerCharacter::KillImpl` `0x1406B6580` — обёртка над ним): ранние выходы —
   отложенное убийство (`Character::sub_1406282C0` → счётчик `FUN_140628280`, событие не шлётся),
   `IsDead(false)` (истинно для kDying, kDead, kRecycle, kEssentialDown; `IsDead(true)` — kDying,
   kDead, kRecycle; `0x1405E3160` id 36484), ребёнок не-игрок. Далее ветвление
   `!bEssentialTakeNoDamage || Character::sub_1405D0F40(target, attacker)` (`0x1405D0F40` id 36247:
   не kProtected, не ребёнок, и либо не kEssential, либо атакующий — игрок для protected):
   - **ветка смерти**: `TESDeathEvent(dead=false)` → `SetLifeState(kDying)`; если нет high-process
     или 3D — сразу `KillImmediate` (то есть `dead=true` в том же вызове), иначе анимация/рэгдолл;
   - **ветка essential**: `SetLifeState(kEssentialDown)`, **TESDeathEvent не шлётся**.
5. `Actor::KillImmediate` `0x1405FB640`: защита `!bEssentialTakeNoDamage || !kEssential`; если бит
   `kSetOnDeath` (boolBits 1<<23) уже стоит — только `SetLifeState(kDead)` без события; иначе ставит
   бит, шлёт `dead=true`, `SetLifeState(kDead)`, гасит бой/пакеты, ставит `kDead` (1<<11).
   Кто вызывает: `KillImpl` (мгновенный путь); обновление high-process `0x14065F040` (id 38606) и
   `0x140660F30` (id 38607) — когда `lifeState==kDying` и таймер умирания истёк; `Actor::MovetoLow`,
   `MovetoMiddleLow`, `MoveToMiddleHigh` (слоты 0xF2–0xF4, ids 36614–36616) — если актёр kDying при смене
   уровня процесса; `TESObjectREFR::Disable` `0x1402986B0` для kDying.

Гарантии и оговорки:

- На одну смерть — ровно одно `dead=false` и одно `dead=true` (защиты `IsDead(false)` в KillImpl и
  `kSetOnDeath` в KillImmediate). Порядок всегда false → true; интервал — длительность анимации
  смерти/рэгдолла, либо ноль (мгновенный путь, оба события подряд в одном кадре).
- Bleedout и essential-down **не** дают TESDeathEvent. `Actor::SetLifeState` `0x1405EDEF0`
  (id 36604; AE `0x140680740` id 37612; VR `0x1405F6600`) при переходе в состояние 7 или 8 шлёт
  `TESEnterBleedoutEvent` (holder+0x420) — проверено на всех трёх рантаймах. Выход из bleedout —
  `HealthUpdateSink` `0x1406227A0` (id 37543) → `SetLifeState(kAlive)` при восстановлении здоровья,
  события нет. Protected в bleedout, добитый игроком, идёт по обычной цепочке (события будут).
- Повтор для того же актёра возможен только через `Actor::Resurrect` `0x1405D5290` (id 36331):
  снимает `kSetOnDeath` и `kDead`, возвращает здоровье через `ModActorValue(kDamage)`,
  `SetLifeState(kAlive)`; следующая смерть снова даёт оба события. Papyrus `Kill` над kEssentialDown
  сначала делает `SetLifeState(kAlive)`, но essential с `bEssentialTakeNoDamage=1` всё равно
  уходит в essential-down без события.
- Загрузка: для уже мёртвых (kDead) актёров события нет — `Actor::FinishLoadGame` `0x1405F4EA0`
  (id 36644) лишь чинит lifeState/контроллер. Актёр, сохранённый в kDying, добивается через
  `MovetoX`/high-process → `dead=true` придёт после загрузки (повторно, если `kSetOnDeath` не был
  восстановлен из сохранения — сериализация boolBits не проверялась). Значит на старте сессии
  состояние мёртвых надо снимать опросом `IsDead()`, а не ждать события.
- Поток. `KillImpl` исполняется только из разгребания очереди пула: `FUN_1405C1B20` ←
  `Main::sub_1405B50E0` ← job «UpdateNonRenderSafeAI» (`Job_Non_render_safe_AI` `0x140640B20`,
  регистрируется в `SetupJobLists` `0x140575210`). Это внутри кадра `Main::Update` (id 35565), но
  через job-систему, то есть **идентичность OS-потока main thread не гарантирована**. `dead=true`
  из обновления high-process — тоже внутри кадра AI. Переход в bleedout/essential-down происходит
  синхронно в потоке вызвавшего `damageav`: для Papyrus `Actor.DamageActorValue` (`0x14094A4C0`
  id 53860, зовёт `HandleHealthDamage` напрямую через vtable) это поток Papyrus VM. Если пул отключён
  (`byte_141DEF8A0 == 0`), задача убийства выполняется синхронно в потоке вызывающего.
  Вывод: в sink только копировать handle/formID и флаг в потокобезопасную очередь, обрабатывать в
  логическом кадре клиента.

## 2. Пути записи health/magicka/stamina

Хранение: `Actor::healthModifiers/magickaModifiers/staminaModifiers` — `float[3]`
(kPermanent=0, kTemporary=1, kDamage=2) по +0x228/+0x234/+0x240, voicePoints +0x24C; остальные AV —
`avStorage` +0x200 (`LocalMap`, поиск по строке ключей под глобальным RW-локом `unk_142F3A2B8`).

Чтение: `GetActorValue` (слот 1, `0x140620D60` id 37517 → `0x1406220B0` id 37533) =
base + permanent + temporary + damage (для AV с флагом AVI 0x40000 сначала кэш AIProcess).
`GetPermanentActorValue` (слот 2, `0x140620E60` id 37518 → `0x1406221E0` id 37535) = base + permanent.
`GetActorValueModifier` `0x140621350` (id 37524; AE `0x1406B29C0` id 38469; VR `0x14062A100`) —
для HP/MP/SP это switch и одно чтение поля.

Единая точка записи модификаторов — `Character::damageav` `0x140621120` (id 37523;
AE `0x1406B27A0` id 38468 — единственный callee `ModActorValue` AE `0x1406B2770`; VR `0x140629ED0`):
`(actor, modifier, av, value, attacker)`. Через неё проходят:

- `ActorValueOwner::ModActorValue` (слот 6, `0x1406210F0` id 37522) ← Papyrus `ModActorValue`
  (kPermanent), `DamageActorValue` (kDamage; `0x14094A4C0`), `ForceActorValue` (`0x14094A9A0`),
  `RestoreActorValue` (`0x14094C1D0` → `Actor::RestoreActorValue` `0x140620900` id 37513: только пока
  damage < 0), console/condition handlers (`0x1402F1FC0` id 21547 и соседи), `Resurrect`;
- регенерация: `Actor::sub_140620610` (health), `sub_140620820` (magicka),
  `CalculateStaminaRegeneration` `0x140620690` → `RestoreActorValue` → `ModActorValue(kDamage)`;
  задержка регенерации — `Update_RegenDelay` `0x140620CC0` (id 37516), выставляется в `sub_1406213D0`;
- магия: `ValueModifierEffect::Func31` `0x140567880` (id 34284, старт эффекта, kDamage) и `Func32`
  `0x140567A80` (id 34286, обновление: kTemporary для эффектов с флагом kRecover, иначе kDamage/
  kPermanent) — вызывают `damageav` напрямую, минуя vtable;
- `Actor::DoDamage` `0x1405D6300` (id 36345, урон с поправкой на сложность) → `damageav(kDamage,
  Health)`; его вызывают обработка попадания `0x140626400` (id 37633), обработчик
  `bhkCharacterMoveFinishEvent` `0x14060B620` (id 36973 — урон от падения), `Actor::Update`
  `0x1405D6FB0` (id 36357), `DoTrap2`, задача BSTaskPool, `HitData`;
- урон по конечностям внутри `0x140626400` — прямой `damageav`.

Внутри `damageav`: kDamage сначала режется `CheckClampDamageModifier` (Actor `0x1406222F0` id 37537:
current не ниже 0 и минимум здоровья essential `fEssentialNPCMinimumHealth`; PlayerCharacter
`0x1406B4670`: god-mode), после записи — `sub_140622150` (сброс кэша AIProcess) и
`Character::sub_1406213D0` `0x1406213D0` (id 37525): задержка регенерации при delta<0 и вызов
таблицы per-AV колбэков `qword_142F39A40` (`[Health]=HealthUpdateSink`, `[Stamina]=StaminaUpdateSink`,
`WardPowerSink`; заполняется в `0x1406215B0` id 37528). Затем при `Health && value<0` —
`HandleHealthDamage(attacker)`.

Мимо `damageav` идут записи **базового** значения: `SetBaseActorValue` (слот 4, `0x140621070`
id 37520 → `0x140623060` id 37561), `ModBaseActorValue` (слот 5, `0x1406210B0` id 37521 →
`0x140622F80` id 37560); `SetActorValue` (слот 7) для Actor — базовая реализация `0x14036D280`,
то есть `SetBaseActorValue`; Papyrus `SetActorValue` (`0x14094C360`) идёт туда же. Обе базовые
записи тоже зовут `sub_140622150` и `sub_1406213D0`. Десериализация модификаторов при загрузке
(change-флаги kDamageModifiers/kOverrideModifiers/kPermanentModifiers) пишет поля напрямую — отдельно
не трассировалась, считается путём мимо обеих точек.

Вывод: единой точки нет, но пара `damageav` + `sub_1406213D0` покрывает все runtime-изменения,
кроме загрузки. Обе — не экспортируемые «горячие» функции: регенерация дёргает их каждый кадр для
каждого high-process актёра, вызовы приходят из нескольких потоков (кадр AI, Papyrus VM).

Стоимость периодического чтения: для HP/MP/SP `GetActorValue` = vcall + проверка флага AVI +
vcall `GetBaseActorValue` (поиск базы в `avStorage` под read-lock) + три сложения; порядок
100–300 нс. Полный снимок (current, permanent, temporary, damage × 3 AV ≈ 12 вызовов) — 1–3 мкс на
актёра. Даже покадрово для игрока это ничтожно; для N отслеживаемых актёров при 2–4 Гц — тем более.

## 3. TESHitEvent — не поток изменений HP

Отправители (holder+0x5D8), SE: хелпер `0x14062D7D0` (id 37735) из обработки попадания
`0x140626400` (id 37633: HitData — ближний/дальний бой, взрывы, ловушки через HitData),
`Actor::CalculateCurrentHitTargetForWeaponSwing` `0x140629090` (id 37674),
`MagicTarget::AddTarget` `0x140633090` (id 37832 — один раз при наложении заклинания),
`Explosion` `0x140739630` (id 42677), `FlameProjectile` `0x140752B30`/`0x1407531C0` (ids 43016/43022).
AE: шесть функций с тем же смещением (`0x1406B7BC0` 38586, `0x1406BADD0` 38628, `0x1406C50A0` 38786,
`0x1407EC640` 44207, `0x1407ECD80` 44213, `0x14072DB30` 40412 — по отдельности не декомпилировались).
VR: `0x140631F60`, `0x14063BF60`, `0x1406B3200`, `0x1407641D0`, `0x14077DE50`, `0x14077E4E0`.

Не дают TESHitEvent, но меняют HP: тики DoT и ядов (`ValueModifierEffect::Func32` → `damageav`
каждый тик; хит-событие лишь однажды в `AddTarget`), урон от падения (`bhkCharacterMoveFinishEvent`
→ `DoDamage`), `Actor::Update` → `DoDamage` (утопление и подобное), Papyrus Damage/Restore/Mod/Set,
console, регенерация, смена базы (уровень, перки, зачарования через kPermanent/базу).

## 4. Максимум и связь модификаторов

HUD-метр `ActorValueMeter::Func5` `0x1408818B0` (id 50765) считает процент как
`GetActorValue / (GetActorValue − GetActorValueModifier(kDamage)) × 100`. Отображаемый максимум =
current − damage = base + permanent + temporary = **`GetPermanentActorValue() +
GetActorValueModifier(kTemporary)`** — формула подтверждена. Fortify Health (эффект с флагом
kRecover) пишется в kTemporary (`Func32` → `damageav(1, …)`), постоянные способности — в kPermanent.
Модификатор damage всегда ≤ 0 (clamp в `damageav`/`CheckClampDamageModifier`), `current = max + damage`;
восстановление — `ModActorValue(kDamage, +x)` только пока damage < 0. Papyrus `GetActorValueMax`
(`0x14094AB00`) читает тот же `0x140621350`.

## 5. Рекомендация

1. **Периодический опрос** из логического кадра клиента (main thread), интервал 250–500 мс,
   с детекцией изменений: на каждый AV снимок `(GetActorValue, GetPermanentActorValue,
   GetActorValueModifier(kTemporary), GetActorValueModifier(kDamage))`, отправка только при отличии
   от предыдущего плюс редкий heartbeat. Потери — только переходные процессы короче интервала
   (просадка и регенерация внутри 250 мс), для UI/телеметрии приемлемо. Хуков не требуется, код
   одинаков для SE/AE/VR.
2. **Ускорение событиями**: sink `TESDeathEvent` (`dead=false` → «умирает», `dead=true` → «мёртв»),
   при необходимости `TESEnterBleedoutEvent`. В sink — только положить `(handle, flag)` в
   потокобезопасную очередь; обработка в логическом кадре, там же немедленный внеочередной снимок AV.
   Воскрешение и состояние на старте сессии — опросом `IsDead()`/lifeState, событий для них нет.
3. **Отклонённые хуки** (риск против покрытия):
   - `Character::damageav` / `sub_1406213D0` — горячий путь (регенерация каждый кадр), три
     рантайма с разными прологами, вызовы из потока AI и Papyrus VM, всё равно не покрывают
     загрузку и базовые записи (`damageav`);
   - патч таблицы `qword_142F39A40[Health]` (замена `HealthUpdateSink`) — data-патч движка, только
     health, ломается любым другим плагином, делающим то же;
   - vtable-хуки `HandleHealthDamage`/`ModActorValue` — по одному на класс (Actor, Character,
     PlayerCharacter), мимо них идут прямые вызовы `damageav` из магии/`DoDamage` и базовые записи;
   - vtable-хук `KillImpl` — не даёт ничего сверх `TESDeathEvent`, но добавляет риск;
   - перерегистрация Papyrus-нативов — покрывает только скриптовый путь.

Что не проверено: путь через BSTaskPool, регенерация и формула HUD — только на SE (на AE/VR
предполагаются такими же); сериализация `kSetOnDeath` в сохранение; AE-отправители TESHitEvent
по отдельности; VR-адреса взяты из таблицы соответствия SE→VR и подтверждены по структуре функций.
