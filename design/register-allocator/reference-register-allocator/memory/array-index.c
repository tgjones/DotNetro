// Indexed load a[i]. The base pointer must stay in a zp pointer pair while the
// scaled index is computed and held in $y, exercising the interaction between
// the pointer class and the index register.
int array_index(const int *a, int i) {
    return a[i] + a[i + 1];
}
