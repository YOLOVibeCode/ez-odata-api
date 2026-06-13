-- Reference fixture (spec 13 §2): northwind-ish core with FKs, composite keys,
-- self-reference, keyless table, view, and a type zoo. Deterministic data.

CREATE TABLE customers (
    id          serial PRIMARY KEY,
    name        text NOT NULL,
    email       varchar(320) NOT NULL UNIQUE,
    country     varchar(2),
    ssn         varchar(11),
    created_at  timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE customers IS 'CRM master table';
COMMENT ON COLUMN customers.ssn IS 'PII: masked for most roles';

CREATE TABLE products (
    id           serial PRIMARY KEY,
    sku          varchar(32) NOT NULL UNIQUE,
    name         text NOT NULL,
    price        numeric(10,2) NOT NULL,
    discontinued boolean NOT NULL DEFAULT false
);

CREATE TABLE orders (
    id          bigserial PRIMARY KEY,
    customer_id int NOT NULL REFERENCES customers(id),
    status      text NOT NULL DEFAULT 'open',
    total       numeric(12,2) NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE order_items (
    order_id    bigint NOT NULL REFERENCES orders(id),
    line_no     int NOT NULL,
    product_id  int NOT NULL REFERENCES products(id),
    qty         int NOT NULL,
    unit_price  numeric(10,2) NOT NULL,
    PRIMARY KEY (order_id, line_no)
);

CREATE TABLE employees (
    id         serial PRIMARY KEY,
    name       text NOT NULL,
    manager_id int REFERENCES employees(id)
);

CREATE TABLE no_pk_log (
    message    text NOT NULL,
    logged_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE type_zoo (
    id        serial PRIMARY KEY,
    a_int2    smallint,
    a_int8    bigint,
    a_num     numeric(18,4),
    a_float4  real,
    a_float8  double precision,
    a_bool    boolean,
    a_uuid    uuid,
    a_date    date,
    a_time    time,
    a_tstz    timestamptz,
    a_bytes   bytea,
    a_json    jsonb,
    a_textarr text[]
);

CREATE VIEW v_customer_order_totals AS
SELECT c.id AS customer_id, c.name, COUNT(o.id) AS order_count, COALESCE(SUM(o.total), 0) AS revenue
FROM customers c LEFT JOIN orders o ON o.customer_id = c.id
GROUP BY c.id, c.name;

INSERT INTO customers (name, email, country, ssn, created_at) VALUES
('Acme Corp',     'ops@acme.example',     'US', '111-22-3333', '2025-01-15T10:00:00Z'),
('Globex',        'it@globex.example',    'DE', '222-33-4444', '2025-02-20T11:30:00Z'),
('Initech',       'admin@initech.example','US', '333-44-5555', '2025-03-05T09:15:00Z'),
('Umbrella',      'lab@umbrella.example', 'JP',  NULL,         '2025-04-10T16:45:00Z'),
('Stark Industries','tony@stark.example', 'US', '444-55-6666', '2025-05-01T08:00:00Z'),
('Wayne Enterprises','bruce@wayne.example','US', NULL,         '2025-06-18T22:10:00Z'),
('Aperture Labs', 'glados@aperture.example','GB','555-66-7777','2025-07-22T13:25:00Z'),
('Tyrell Corp',   'roy@tyrell.example',   'US', NULL,          '2026-01-30T07:55:00Z');

INSERT INTO products (sku, name, price, discontinued) VALUES
('X-1',  'Widget Standard', 19.99, false),
('X-2',  'Widget Pro',      49.99, false),
('Y-9',  'Gadget Mini',      9.50, false),
('Z-3',  'Gizmo Classic',  129.00, true),
('Q-7',  'Doohickey Max',   75.25, false);

INSERT INTO orders (customer_id, status, total, created_at) VALUES
(1, 'open',      89.48, '2026-03-01T10:00:00Z'),
(1, 'shipped',  258.00, '2026-03-05T11:00:00Z'),
(2, 'open',     149.97, '2026-03-07T12:00:00Z'),
(2, 'cancelled',  9.50, '2026-03-08T13:00:00Z'),
(3, 'shipped',   75.25, '2026-03-10T14:00:00Z'),
(4, 'open',     519.96, '2026-04-02T15:00:00Z'),
(5, 'shipped',   19.99, '2026-04-15T16:00:00Z'),
(5, 'open',     279.49, '2026-05-20T17:00:00Z'),
(7, 'open',      28.99, '2026-06-01T18:00:00Z'),
(8, 'shipped',  158.50, '2026-06-10T19:00:00Z');

INSERT INTO order_items (order_id, line_no, product_id, qty, unit_price) VALUES
(1, 1, 1, 2, 19.99), (1, 2, 3, 1, 9.50), (1, 3, 2, 1, 39.99),
(2, 1, 4, 2, 129.00),
(3, 1, 2, 3, 49.99),
(4, 1, 3, 1, 9.50),
(5, 1, 5, 1, 75.25),
(6, 1, 4, 4, 129.00), (6, 2, 3, 1, 3.96),
(7, 1, 1, 1, 19.99),
(8, 1, 2, 5, 49.99), (8, 2, 3, 3, 9.50),
(9, 1, 3, 2, 9.50), (9, 2, 1, 1, 9.99),
(10, 1, 2, 2, 49.99), (10, 2, 4, 1, 58.52);

INSERT INTO employees (name, manager_id) VALUES
('Diana Prince', NULL),
('Clark Kent', 1),
('Barry Allen', 1),
('Hal Jordan', 2);

INSERT INTO no_pk_log (message, logged_at) VALUES
('boot', '2026-01-01T00:00:00Z'),
('ready', '2026-01-01T00:00:05Z');

INSERT INTO type_zoo (a_int2, a_int8, a_num, a_float4, a_float8, a_bool, a_uuid, a_date, a_time, a_tstz, a_bytes, a_json, a_textarr) VALUES
(7, 9007199254740993, 1234.5678, 1.5, 2.25, true,
 'a1b2c3d4-e5f6-4711-8899-aabbccddeeff', '2026-06-12', '13:45:30',
 '2026-06-12T13:45:30Z', '\xdeadbeef', '{"k": "v", "n": 5}', ARRAY['alpha','beta']);
