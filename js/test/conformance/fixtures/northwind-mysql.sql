-- Northwind fixture adapted for MySQL 8 (spec 13 §2).
-- SERIAL → INT AUTO_INCREMENT
-- timestamptz → DATETIME
-- boolean → TINYINT(1)
-- numeric → DECIMAL
-- bytea → BLOB, jsonb → JSON, uuid → VARCHAR(36), text[] → JSON

CREATE TABLE customers (
    id          INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name        TEXT NOT NULL,
    email       VARCHAR(320) NOT NULL UNIQUE,
    country     VARCHAR(2),
    ssn         VARCHAR(11),
    created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE products (
    id           INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    sku          VARCHAR(32) NOT NULL UNIQUE,
    name         TEXT NOT NULL,
    price        DECIMAL(10,2) NOT NULL,
    discontinued TINYINT(1) NOT NULL DEFAULT 0
);

CREATE TABLE orders (
    id          BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    customer_id INT NOT NULL,
    status      VARCHAR(50) NOT NULL DEFAULT 'open',
    total       DECIMAL(12,2) NOT NULL DEFAULT 0,
    created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (customer_id) REFERENCES customers(id)
);

CREATE TABLE order_items (
    order_id    BIGINT NOT NULL,
    line_no     INT NOT NULL,
    product_id  INT NOT NULL,
    qty         INT NOT NULL,
    unit_price  DECIMAL(10,2) NOT NULL,
    PRIMARY KEY (order_id, line_no),
    FOREIGN KEY (order_id) REFERENCES orders(id),
    FOREIGN KEY (product_id) REFERENCES products(id)
);

CREATE TABLE employees (
    id         INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name       TEXT NOT NULL,
    manager_id INT,
    FOREIGN KEY (manager_id) REFERENCES employees(id)
);

CREATE TABLE no_pk_log (
    message    TEXT NOT NULL,
    logged_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE type_zoo (
    id        INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    a_int2    SMALLINT,
    a_int8    BIGINT,
    a_num     DECIMAL(18,4),
    a_float4  FLOAT,
    a_float8  DOUBLE,
    a_bool    TINYINT(1),
    a_uuid    VARCHAR(36),
    a_date    DATE,
    a_time    TIME,
    a_tstz    DATETIME,
    a_bytes   BLOB,
    a_json    JSON,
    a_textarr JSON
);

CREATE VIEW v_customer_order_totals AS
SELECT c.id AS customer_id, c.name, COUNT(o.id) AS order_count, COALESCE(SUM(o.total), 0) AS revenue
FROM customers c LEFT JOIN orders o ON o.customer_id = c.id
GROUP BY c.id, c.name;

INSERT INTO customers (name, email, country, ssn, created_at) VALUES
('Acme Corp',     'ops@acme.example',     'US', '111-22-3333', '2025-01-15 10:00:00'),
('Globex',        'it@globex.example',    'DE', '222-33-4444', '2025-02-20 11:30:00'),
('Initech',       'admin@initech.example','US', '333-44-5555', '2025-03-05 09:15:00'),
('Umbrella',      'lab@umbrella.example', 'JP',  NULL,         '2025-04-10 16:45:00'),
('Stark Industries','tony@stark.example', 'US', '444-55-6666', '2025-05-01 08:00:00'),
('Wayne Enterprises','bruce@wayne.example','US', NULL,         '2025-06-18 22:10:00'),
('Aperture Labs', 'glados@aperture.example','GB','555-66-7777','2025-07-22 13:25:00'),
('Tyrell Corp',   'roy@tyrell.example',   'US', NULL,          '2026-01-30 07:55:00');

INSERT INTO products (sku, name, price, discontinued) VALUES
('X-1',  'Widget Standard', 19.99, 0),
('X-2',  'Widget Pro',      49.99, 0),
('Y-9',  'Gadget Mini',      9.50, 0),
('Z-3',  'Gizmo Classic',  129.00, 1),
('Q-7',  'Doohickey Max',   75.25, 0);

INSERT INTO orders (customer_id, status, total, created_at) VALUES
(1, 'open',      89.48, '2026-03-01 10:00:00'),
(1, 'shipped',  258.00, '2026-03-05 11:00:00'),
(2, 'open',     149.97, '2026-03-07 12:00:00'),
(2, 'cancelled',  9.50, '2026-03-08 13:00:00'),
(3, 'shipped',   75.25, '2026-03-10 14:00:00'),
(4, 'open',     519.96, '2026-04-02 15:00:00'),
(5, 'shipped',   19.99, '2026-04-15 16:00:00'),
(5, 'open',     279.49, '2026-05-20 17:00:00'),
(7, 'open',      28.99, '2026-06-01 18:00:00'),
(8, 'shipped',  158.50, '2026-06-10 19:00:00');

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
('boot', '2026-01-01 00:00:00'),
('ready', '2026-01-01 00:00:05');

INSERT INTO type_zoo (a_int2, a_int8, a_num, a_float4, a_float8, a_bool, a_uuid, a_date, a_time, a_tstz, a_bytes, a_json, a_textarr) VALUES
(7, 9007199254740993, 1234.5678, 1.5, 2.25, 1,
 'a1b2c3d4-e5f6-4711-8899-aabbccddeeff', '2026-06-12', '13:45:30',
 '2026-06-12 13:45:30', UNHEX('deadbeef'), JSON_OBJECT('k', 'v', 'n', 5), JSON_ARRAY('alpha', 'beta'));
