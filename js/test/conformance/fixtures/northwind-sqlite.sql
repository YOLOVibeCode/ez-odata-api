-- Northwind fixture adapted for SQLite (spec 13 §2).
-- SERIAL → INTEGER PRIMARY KEY AUTOINCREMENT
-- timestamptz → DATETIME
-- boolean → INTEGER (0/1)
-- numeric → DECIMAL / REAL
-- bytea → BLOB, jsonb → TEXT, uuid → TEXT, text[] → TEXT

PRAGMA foreign_keys = ON;

CREATE TABLE customers (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL,
    email       TEXT NOT NULL UNIQUE,
    country     TEXT,
    ssn         TEXT,
    created_at  DATETIME NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE products (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    sku          TEXT NOT NULL UNIQUE,
    name         TEXT NOT NULL,
    price        DECIMAL(10,2) NOT NULL,
    discontinued BOOLEAN NOT NULL DEFAULT 0
);

CREATE TABLE orders (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    customer_id INTEGER NOT NULL REFERENCES customers(id),
    status      TEXT NOT NULL DEFAULT 'open',
    total       DECIMAL(12,2) NOT NULL DEFAULT 0,
    created_at  DATETIME NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE order_items (
    order_id    INTEGER NOT NULL REFERENCES orders(id),
    line_no     INTEGER NOT NULL,
    product_id  INTEGER NOT NULL REFERENCES products(id),
    qty         INTEGER NOT NULL,
    unit_price  DECIMAL(10,2) NOT NULL,
    PRIMARY KEY (order_id, line_no)
);

CREATE TABLE employees (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT NOT NULL,
    manager_id INTEGER REFERENCES employees(id)
);

CREATE TABLE no_pk_log (
    message    TEXT NOT NULL,
    logged_at  DATETIME NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE type_zoo (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    a_int2    INTEGER,
    a_int8    INTEGER,
    a_num     DECIMAL(18,4),
    a_float4  REAL,
    a_float8  REAL,
    a_bool    BOOLEAN,
    a_uuid    TEXT,
    a_date    DATE,
    a_time    TIME,
    a_tstz    DATETIME,
    a_bytes   BLOB,
    a_json    TEXT,
    a_textarr TEXT
);

CREATE VIEW v_customer_order_totals AS
SELECT c.id AS customer_id, c.name, COUNT(o.id) AS order_count, COALESCE(SUM(o.total), 0) AS revenue
FROM customers c LEFT JOIN orders o ON o.customer_id = c.id
GROUP BY c.id, c.name;

INSERT INTO customers (name, email, country, ssn, created_at) VALUES
('Acme Corp',     'ops@acme.example',     'US', '111-22-3333', '2025-01-15T10:00:00'),
('Globex',        'it@globex.example',    'DE', '222-33-4444', '2025-02-20T11:30:00'),
('Initech',       'admin@initech.example','US', '333-44-5555', '2025-03-05T09:15:00'),
('Umbrella',      'lab@umbrella.example', 'JP',  NULL,         '2025-04-10T16:45:00'),
('Stark Industries','tony@stark.example', 'US', '444-55-6666', '2025-05-01T08:00:00'),
('Wayne Enterprises','bruce@wayne.example','US', NULL,         '2025-06-18T22:10:00'),
('Aperture Labs', 'glados@aperture.example','GB','555-66-7777','2025-07-22T13:25:00'),
('Tyrell Corp',   'roy@tyrell.example',   'US', NULL,          '2026-01-30T07:55:00');

INSERT INTO products (sku, name, price, discontinued) VALUES
('X-1',  'Widget Standard', 19.99, 0),
('X-2',  'Widget Pro',      49.99, 0),
('Y-9',  'Gadget Mini',      9.50, 0),
('Z-3',  'Gizmo Classic',  129.00, 1),
('Q-7',  'Doohickey Max',   75.25, 0);

INSERT INTO orders (customer_id, status, total, created_at) VALUES
(1, 'open',      89.48, '2026-03-01T10:00:00'),
(1, 'shipped',  258.00, '2026-03-05T11:00:00'),
(2, 'open',     149.97, '2026-03-07T12:00:00'),
(2, 'cancelled',  9.50, '2026-03-08T13:00:00'),
(3, 'shipped',   75.25, '2026-03-10T14:00:00'),
(4, 'open',     519.96, '2026-04-02T15:00:00'),
(5, 'shipped',   19.99, '2026-04-15T16:00:00'),
(5, 'open',     279.49, '2026-05-20T17:00:00'),
(7, 'open',      28.99, '2026-06-01T18:00:00'),
(8, 'shipped',  158.50, '2026-06-10T19:00:00');

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
('boot', '2026-01-01T00:00:00'),
('ready', '2026-01-01T00:00:05');

INSERT INTO type_zoo (a_int2, a_int8, a_num, a_float4, a_float8, a_bool, a_uuid, a_date, a_time, a_tstz, a_bytes, a_json, a_textarr) VALUES
(7, 9007199254740993, 1234.5678, 1.5, 2.25, 1,
 'a1b2c3d4-e5f6-4711-8899-aabbccddeeff', '2026-06-12', '13:45:30',
 '2026-06-12T13:45:30', X'deadbeef', '{"k": "v", "n": 5}', '["alpha","beta"]');
