-- Database per service, on one server. Separate servers would prove nothing extra and cost four
-- containers: what matters is that no service can read another's schema, and separate databases do that.
CREATE DATABASE orderdb;
CREATE DATABASE paymentdb;
CREATE DATABASE inventorydb;
CREATE DATABASE shippingdb;
