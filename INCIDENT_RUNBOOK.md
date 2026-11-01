# Incident runbook

Що робити під час інциденту. Кожен сценарій має чотири кроки: симптом, підтвердження, дія, перевірка відновлення. Команди виконуються з кореня репозиторію в bash.

Хости стандартні: сервіс `localhost:8080`, gateway `localhost:4000`, консоль `localhost:4200`. Якщо стек піднятий з `demo-ports.yml`, у командах заміни `8080` на `8180` і `4000` на `4100`, а консоль буде на `localhost:4300`. Команди `docker compose exec` від портів не залежать.

Час у лозі (`created_at`) в UTC.

## Підготовка: свіжий лог

Два-три збої серед тисяч рядків майже не зрушать плитку error rate, тож відтворення починаємо з порожньої бази. `down -v` стирає лог, чергу підтверджень і кеш.

```bash
docker compose down -v
```

```bash
docker compose up --build -d
```

Чекаємо сервіс і gateway. Холодний LiteLLM віддає 500 на перший запит, тому чекаємо саме його відповідь, а не лише `/health` сервісу.

```bash
until curl -sf localhost:8080/health >/dev/null; do sleep 2; done; until curl -s -o /dev/null -w "%{http_code}" -X POST localhost:4000/v1/chat/completions -H "Content-Type: application/json" -d '{"model":"mock","messages":[{"role":"user","content":"hi"}]}' | grep -q 200; do sleep 3; done; echo ready
```

## Сценарій 1. Збій провайдера (503)

### 0. Нормальна робота

```bash
curl -s localhost:8080/chat -H "Content-Type: application/json" -d '{"message":"How do I reset my login password?"}'
```

Очікувано відповідь по суті («Щоб скинути пароль…»), `"degraded":false`.

### 1. Симптом

Відтворюємо збій двома зверненнями з маркером `__fail_503`. Mock віддає 503 на кожну модель ланцюга.

```bash
curl -s -w "\nHTTP %{http_code}\n" localhost:8080/chat -H "Content-Type: application/json" -d '{"message":"__fail_503 How do I reset my login password?"}'
```

```bash
curl -s -w "\nHTTP %{http_code}\n" localhost:8080/chat -H "Content-Type: application/json" -d '{"message":"__fail_503 I cannot login to my account"}'
```

Користувач бачить ввічливу заглушку «Вибачте, тимчасові проблеми на нашому боці…» з `HTTP 200` і `"degraded":true`, а не помилку 500. Для оператора симптом це скарги «бот вибачається» і червона плитка ERROR RATE.

### 2. Підтвердження

```bash
curl -s localhost:8080/observability
```

`fallback_events` виріс на 2 (по одному переходу на звернення), `error_rate_pct` злетів з 0 до 80.

```bash
curl -s localhost:8080/providers
```

Моделі ланцюга `degraded`, після трьох збоїв поспіль `down`. У консолі це картка «Надійність провайдерів».

```bash
docker compose exec postgres psql -U llmops -d llmops -c "SELECT created_at::time(0) AS at, model, attempt, status, error_kind FROM requests ORDER BY id DESC LIMIT 5;"
```

На кожне звернення два рядки зі статусом `503` і `error_kind = status`: `attempt 1` основна модель, `attempt 2` резервна. Це збій провайдера, а не наш код. Якби винна була база, у `curl -s localhost:8080/errors` ріс би `db_failure`, а `/observability` віддавав би 503.

### 3. Дія

Руками нічого. Fallback і деградація вже працюють. Перевіряємо лише, що вони справді спрацювали: клієнт отримує 200 із заглушкою, кожна модель ланцюга має рядок у лозі, запобіжник відкривається після трьох збоїв і далі не смикає провайдера на кожне звернення.

Не відкочуємо реліз. Збій почався не з нашою зміною (див. «Rollback-критерії» в README). Якщо `/providers` показує `down` для всіх моделей ланцюга довше 10 хвилин, це вже ескалація до провайдера, а не дія в цьому сервісі.

Кеш чистити не треба. Заглушки й відповіді не 200 у кеш не пишуться, тож після збою кеш не віддає «тимчасові проблеми». Якщо все ж треба (наприклад, після ручного втручання в кеш), кеш у памʼяті скидає `docker compose restart service`. Рестарт стирає ще й лічильники `/errors` і чергу підтверджень. З Redis (`--profile advanced`, `REDIS_URL=redis:6379`) замість рестарту `docker compose exec redis redis-cli FLUSHDB`.

### 4. Перевірка відновлення

Тільки **нове** питання, якого ще немає в кеші. Повтор першого питання відповів би з кешу (`"cache":"hit"`, затримка кілька мс) навіть посеред інциденту.

```bash
curl -s -w "\nHTTP %{http_code}\n" localhost:8080/chat -H "Content-Type: application/json" -d '{"message":"Hello! I forgot my login, what should I do?"}'
```

Відповідь по суті, `"cache":"miss"`, `"degraded":false`.

```bash
curl -s localhost:8080/observability
```

`fallback_events` більше не росте (він накопичувальний і не спадає ніколи), а `error_rate_pct` іде вниз. Якщо запобіжник встиг відкритися, відповідь повернеться з першою пробою half-open, тобто не пізніше ніж за 5 с.

## Хронологія інциденту 2026-11-01

Відтворення командами сценарію 1 на свіжому лозі. Час локальний.

| Час | Подія |
|---|---|
| 13:53:38 | Почався інцидент. Два звернення з `__fail_503`, обидві моделі ланцюга (`mock-mini`, `mock`) відповіли 503 |
| 13:53:38 | Виявили з консолі, без скарг. ERROR RATE 0 → 80%, FALLBACK 0 → 2, «Надійність провайдерів» `degraded` |
| 13:53:38 | Користувач бачив ввічливу заглушку з HTTP 200 за 33–46 мс. Помилки 500 не було |
| 13:53:43 | Система відновилась. Нове питання (`cache: miss`) отримало відповідь по суті, FALLBACK лишився 2, ERROR RATE 80 → 66.7% |

**Що зробимо, щоб не повторилось.** Сам збій провайдера ми не відвернемо, тому механізми про те, щоб клієнт його не відчув і ми дізнались першими.
1. Резервна модель на іншому провайдері. На стенді обидві моделі ланцюга живуть на тому самому mock, тож fallback перейшов на модель, що лежала разом з основною, і врятувати відповідь не міг. З реальним ключем ланцюг уже веде на модель іншого провайдера (`service/Program.cs`, `FallbackChain`).
2. Алерт замість погляду на плитку. `monitoring/alerts.yml` піднімає `ErrorRateHigh` (понад 5% за 5 хв) і `ProviderDown` (запобіжник відкритий понад хвилину).
3. Регресійний кейс уже є. Прогін датасету з префіксом `__fail_503 ` має давати 0 зелених, крім відмов guardrail, а кейси з `forbid` на текст заглушки не дають evals зазеленіти під час outage (README, «Evals»).

**Як ми дізнались.** З консолі, до скарг: плитки ERROR RATE і FALLBACK та картка провайдерів. Але плитки рахують увесь день, і на денному трафіку кілька хвилин збою ледь зрушать error rate. Тому для реального трафіку сигнал дає вікно 5 хв у `/metrics` і алерт у Prometheus, а не денна плитка.

## Сценарій 2. Погіршення якості після зміни промпта

HTTP-метрики тут у нормі, тож плитки мовчать. Сигнал дають evals і зміст відповідей.

Відтворення. Активуємо «зламану» версію `v1`.

```bash
curl -s -X POST localhost:8080/prompts/v1/activate
```

### 1. Симптом

```bash
curl -s localhost:8080/chat -H "Content-Type: application/json" -d '{"message":"Thanks, and how do I change my login email?"}'
```

Відповідь «не знаю» при `"degraded":false` і статусі 200. ERROR RATE не росте, скарги «бот нічого не знає».

### 2. Підтвердження

```bash
docker compose exec postgres psql -U llmops -d llmops -c "SELECT version, actor, activated_at FROM prompt_activations ORDER BY id DESC LIMIT 3;" -c "SELECT prompt_version, count(*) FROM requests WHERE created_at > now() - interval '1 hour' GROUP BY 1;"
```

Видно, хто і коли перемкнув версію, і що погані відповіді йдуть на новій версії.

```bash
SERVICE_URL=http://localhost:8080 python evals/run.py --dataset evals/golden.jsonl --threshold $(sh evals/threshold.sh evals/golden.jsonl)
```

Очікувано `eval: 12/16 passed, threshold 14`, падають `faq-1`, `faq-2`, `caps-1`, `thanks-1`.

### 3. Дія. Відкат промпта

```bash
curl -s -X POST localhost:8080/prompts/v2/activate
```

Без деплою, менше хвилини. Застарілих відповідей кеш не віддасть, бо версія промпта входить у ключ кешу.

### 4. Перевірка відновлення

```bash
SERVICE_URL=http://localhost:8080 python evals/run.py --dataset evals/golden.jsonl --threshold $(sh evals/threshold.sh evals/golden.jsonl)
```

Очікувано `eval: 16/16 passed`. У продакшені такий реліз не дійшов би до клієнтів, бо CI-гейт червоніє на тому самому `v1` (README, «CI-гейт»).

## Дашборд і алерти

```bash
docker compose --profile advanced up -d
```

Grafana на `localhost:3000` (без логіну, лише перегляд), дашборд «LLMOps · SupportGW» відкривається одразу. Prometheus на `localhost:9090`, алерти на `localhost:9090/alerts`. Під час сценарію 1 видно сплеск на панелях «Error rate» і «Fallback за хвилину» та червоні «Провайдери».
