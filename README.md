# Starter repository

Те, що студент клонує на першому уроці й дороблює до capstone.

**➡ Повний гайд для студента (вимоги, запуск, кожна папка, troubleshooting): [GETTING_STARTED.md](GETTING_STARTED.md).**
**➡ Домашні завдання — окремий файл на кожен тиждень: [homework/](homework/README.md).**

## Що дається готовим, що добудовує студент

| Компонент | Дається готовим | Студент добудовує |
|---|---|---|
| UI (Angular) | готові в'юхи: Chat + Console/Observability | не змінює |
| App service (.NET) | skeleton | control plane: routing, fallback, cost, policies |
| Observability API (.NET) | skeleton | агрегати з Postgres (traces, p95, cost, error-taxonomy) у Console-в'юху |
| Gateway (LiteLLM) | базовий конфіг | моделі/провайдери, порядок fallback |
| Mock provider | готовий | використовує для тестів і failure-сценаріїв |
| Postgres | базова схема | розширює під logs / cost / prompt records |
| Eval runner (Python) | skeleton | dataset, graders, пороги |
| CI (GitHub Actions) | шаблон `.github/workflows/eval-gate.yml` | eval gate |
| Redis / дашборди | опційний шаблон | advanced extension |

## Запуск (мінімальний стек)

```sh
docker compose up --build
```

Піднімаються: UI (`:4200`), сервіс (`:8080`), LiteLLM (`:4000`), Postgres, mock provider.
Для mock реальні ключі не потрібні. Redis і дашборди — опційні: `docker compose --profile advanced up`.
Для оцінки якості (model-based evals): `cp gateway/.env.example gateway/.env` і заповнити ключі.

## Операційні рішення

Дублюють опис PR тижня 3, щоб перевірка не залежала від платформи.

**Таймаут tool-виклику.** У кожного інструмента свій таймаут у реєстрі (`service/Program.cs`, `toolRegistry`): `lookup_order` 2 с, `create_ticket` 5 с. Для читання вичерпання дає «спробуйте пізніше», і повтор безпечний. Для незворотної дії таймаут означає невідомий результат, бо тікет міг створитись. Тому повтор із тим самим ключем дію не виконує, а користувач отримує «оператор перевірить».

**Ключ ідемпотентності.** `hash(conversation_id, tool, args)` з відсортованими полями аргументів. Незворотна дія спершу резервує ключ в унікальному індексі `tool_calls.idempotency_key` і лише потім виконується. Повтор із тим самим ключем повертає збережений результат, тож два однакові `create_ticket` в одній розмові дають один тікет.

Чесна межа. UI не шле `conversation_id`, і тоді ключ будується від `request_id`. Він закриває лише повтор у межах одного запиту (наш власний ретрай чи fallback). Подвійний клік користувача це вже новий `request_id` і другий тікет. Надалі ключ має присилати сторона, що викликає, наприклад заголовком `Idempotency-Key`.

**Незворотні дії.** Поки `create_ticket` виконується автономно. Контракт на тиждень 4: незворотне → заявка в `/approvals` → виконання після підтвердження людиною.
