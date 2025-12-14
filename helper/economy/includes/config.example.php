<?php
/**
 * config.example.php
 *
 * Helpers configuration
 * Rename this file as "config.php" before editing.
 *
 * @package		magicoli/opensim-helpers
 * @author 		Gudule Lapointe <gudule@speculoos.world>
 * @link 			https://github.com/magicoli/opensim-helpers
 * @license		AGPLv3
 */

/**
 * Main database.
 * For grids, use Robust database credentials.
 * For standalone simulators, use OpenSim database credentials.
 *
 * Access to OpenSim database is required
 *   - for search including classifieds
 *   - for offline messages processing
 *   - for economy
 * It is not required if only search is needed, without classifieds (e.g. to for
 * a multi-grid search engine). In this case search will only provide results
 * for places, land for sale and events.
 */
define( 'OPENSIM_DB', true ); // Set to false for search only, see above
define( 'OPENSIM_DB_HOST', 'localhost' );
define( 'OPENSIM_DB_NAME', 'opensim' );
define( 'OPENSIM_DB_USER', 'opensim' );
define( 'OPENSIM_DB_PASS', 'password' );
define( 'SEARCH_TABLE_EVENTS', 'events' );

/**
 * Search database credentials and settings.
 * Needed if you enable search in OpenSim server.
 *
 * A dedicated database is:
 *   - strongly recommended if the search engine is shared by several grids
 *   - recommended and more efficient for large and/or hypergrid-enabled grids
 *   - optional for closed grids and standalone simulators
 * These are recommendations, the Robust database can safely be used instead.
 */
define( 'SEARCH_DB_HOST', OPENSIM_DB_HOST );
define( 'SEARCH_DB_NAME', OPENSIM_DB_NAME );
define( 'SEARCH_DB_USER', OPENSIM_DB_USER );
define( 'SEARCH_DB_PASS', OPENSIM_DB_PASS );

/**
 * Currency database credentials and settings.
 * Needed if currency is enabled on OpenSim server.
 * A dedicated database is recommended, but not mandatory.
 */
define( 'CURRENCY_DB_HOST', OPENSIM_DB_HOST );
define( 'CURRENCY_DB_NAME', OPENSIM_DB_NAME );
define( 'CURRENCY_DB_USER', OPENSIM_DB_USER );
define( 'CURRENCY_DB_PASS', OPENSIM_DB_PASS );
define( 'CURRENCY_MONEY_TBL', 'balances' );
define( 'CURRENCY_TRANSACTION_TBL', 'transactions' );

/**
 * Money Server settings.
 */
define( 'CURRENCY_USE_MONEYSERVER', false );
define( 'CURRENCY_SCRIPT_KEY', '123456789' );
define( 'CURRENCY_RATE', 10 ); // amount in dollar...
define( 'CURRENCY_RATE_PER', 1000 ); // ... for this amount in virtual currency
define( 'CURRENCY_PROVIDER', null ); // NULL, 'podex' or 'gloebit'
define( 'CURRENCY_HELPER_URL', 'http://yourgrid.org/helpers/' );
// if (!defined('CURRENCY_HELPER_PATH')) define('CURRENCY_HELPER_PATH', dirname(__DIR__));

/**
 * DO NOT MAKE CHANGES BELOW THIS
 * Add your custom values above.
 */
require_once 'databases.php';
require_once 'functions.php';

$currency_addon = dirname( __DIR__ ) . '/addons/' . CURRENCY_PROVIDER . '.php';
if ( file_exists( $currency_addon ) ) {
	require_once $currency_addon;
}
