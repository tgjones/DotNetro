	.zeropage	__rc0
	.zeropage	__rc1
	.zeropage	__rc2
	.zeropage	__rc3
	.zeropage	__rc4
	.zeropage	__rc5
	.zeropage	__rc6
	.zeropage	__rc7
	.zeropage	__rc8
	.zeropage	__rc9
	.zeropage	__rc10
	.zeropage	__rc11
	.zeropage	__rc12
	.zeropage	__rc13
	.zeropage	__rc14
	.zeropage	__rc15
	.zeropage	__rc16
	.zeropage	__rc17
	.zeropage	__rc18
	.zeropage	__rc19
	.zeropage	__rc20
	.zeropage	__rc21
	.zeropage	__rc22
	.zeropage	__rc23
	.zeropage	__rc24
	.zeropage	__rc25
	.zeropage	__rc26
	.zeropage	__rc27
	.zeropage	__rc28
	.zeropage	__rc29
	.zeropage	__rc30
	.zeropage	__rc31
	.file	"common-subexpr.c"
	.section	.text.common_subexpr,"ax",@progbits
	.globl	common_subexpr                  ; -- Begin function common_subexpr
	.type	common_subexpr,@function
common_subexpr:                         ; @common_subexpr
; %bb.0:
	stx	__rc4
	ldx	__rc6
	ldy	__rc7
	clc
	adc	__rc2
	sta	__rc6
	lda	__rc4
	adc	__rc3
	sta	__rc4
	lda	__rc5
	bmi	.LBB0_4
; %bb.1:
	stx	__rc2
	sty	__rc3
	stx	__rc5
	ldx	#255
	dec	__rc2
	cpx	__rc2
	bne	.LBB0_3
; %bb.2:
	dec	__rc3
.LBB0_3:
	lda	__rc6
	sta	g2
	ldx	__rc4
	stx	g2+1
	ldx	__rc5
	stx	g3
	sty	g3+1
	ldx	__rc2
	ldy	__rc3
	jmp	.LBB0_7
.LBB0_4:
	inx
	bne	.LBB0_6
; %bb.5:
	iny
.LBB0_6:
	lda	__rc6
	sta	g1
	lda	__rc4
	sta	g1+1
.LBB0_7:
	stx	g0
	sty	g0+1
	rts
.Lfunc_end0:
	.size	common_subexpr, .Lfunc_end0-common_subexpr
                                        ; -- End function
	.type	g0,@object                      ; @g0
	.section	.bss.g0,"aw",@nobits
	.globl	g0
g0:
	.short	0                               ; 0x0
	.size	g0, 2

	.type	g1,@object                      ; @g1
	.section	.bss.g1,"aw",@nobits
	.globl	g1
g1:
	.short	0                               ; 0x0
	.size	g1, 2

	.type	g2,@object                      ; @g2
	.section	.bss.g2,"aw",@nobits
	.globl	g2
g2:
	.short	0                               ; 0x0
	.size	g2, 2

	.type	g3,@object                      ; @g3
	.section	.bss.g3,"aw",@nobits
	.globl	g3
g3:
	.short	0                               ; 0x0
	.size	g3, 2

	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that there is something in .bss, so it may need to be zeroed.
	.globl	__do_zero_bss
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
